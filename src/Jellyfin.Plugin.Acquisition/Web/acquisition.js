// Jellyfin Acquisition Plugin Client Extension for Jellyfin Web 12.2
(() => {
    let currentAbortController = null;
    let debounceTimer = null;
    let lastQuery = '';
    let activeSearchInput = null;
    // The section and the query it shows; React may re-render the search page and drop or reorder foreign nodes
    let currentSection = null;
    let renderedQuery = '';
    // What the section shows: "requests" (empty search box) or "external" (results for the typed query)
    let sectionMode = '';
    let requestsLoading = false;
    let requestsRetryAfter = 0;

    // "auto" follows the installed theme: Abyss defines --abyss-accent on :root, classic Jellyfin does not
    function resolveStyle() {
        const configured = String((typeof window !== 'undefined' && window.__ACQUISITION_STYLE__) || 'auto').toLowerCase();
        if (configured === 'abyss' || configured === 'jellyfin') return configured;
        try {
            const accent = typeof getComputedStyle === 'function'
                ? getComputedStyle(document.documentElement).getPropertyValue('--abyss-accent').trim()
                : '';
            return accent ? 'abyss' : 'jellyfin';
        } catch (err) {
            return 'jellyfin';
        }
    }

    function themeClassName(baseClass) {
        return `${baseClass} acq-theme-${resolveStyle()}`;
    }

    function getApiBase() {
        return (typeof window !== 'undefined' && window.__ACQUISITION_BASE_URL__) || '';
    }

    // The server may serialize enums by name ("Series") or by number (1); the client works with names
    const KIND_NAMES = ['Movie', 'Series'];
    const STATUS_NAMES = ['Pending', 'Searching', 'Downloading', 'Importing', 'Available', 'RequiresAttention', 'Failed'];
    const STATUS_LABELS = {
        Pending: 'Pendiente',
        Searching: 'Buscando',
        Downloading: 'Descargando',
        Importing: 'Importando',
        Available: 'Disponible',
        RequiresAttention: 'Requiere atención',
        Failed: 'Fallida'
    };

    function kindName(value) {
        return typeof value === 'number' ? KIND_NAMES[value] : value;
    }

    function statusName(value) {
        return typeof value === 'number' ? STATUS_NAMES[value] : value;
    }

    function authValue(value) {
        return String(value ?? '').replace(/"/g, '');
    }

    function getAuthHeaders() {
        const headers = {
            'Content-Type': 'application/json',
            'Accept': 'application/json; profile="CamelCase", application/json;q=0.9, */*;q=0.8'
        };
        const api = typeof window !== 'undefined' ? window.ApiClient : null;
        const token = api && api.accessToken ? api.accessToken() : '';
        if (token) {
            // Reuse the web client's own identity so the server sees the same device/session, not a new one
            const client = (api.appName && api.appName()) || 'Jellyfin Web';
            const version = (api.appVersion && api.appVersion()) || '';
            const deviceName = (api.deviceName && api.deviceName()) || 'Browser';
            const deviceId = (api.deviceId && api.deviceId()) || '';
            headers['Authorization'] = `MediaBrowser Client="${authValue(client)}", Device="${authValue(deviceName)}", DeviceId="${authValue(deviceId)}", Version="${authValue(version)}", Token="${authValue(token)}"`;
        }
        return headers;
    }

    function normalizeCatalogItem(item) {
        if (!item || typeof item !== 'object') return item;
        const providerIds = item.providerIds || item.ProviderIds || {};
        return {
            id: item.id ?? item.Id ?? '',
            kind: kindName(item.kind !== undefined ? item.kind : item.Kind),
            title: item.title ?? item.Title ?? '',
            year: item.year !== undefined ? item.year : item.Year,
            overview: item.overview ?? item.Overview ?? '',
            posterUrl: item.posterUrl ?? item.PosterUrl ?? '',
            providerIds: {
                tvdb: providerIds.tvdb ?? providerIds.Tvdb ?? null,
                tmdb: providerIds.tmdb ?? providerIds.Tmdb ?? null,
                imdb: providerIds.imdb ?? providerIds.Imdb ?? null
            },
            isLocal: Boolean(item.isLocal ?? item.IsLocal),
            localItemId: item.localItemId ?? item.LocalItemId ?? null,
            requestId: item.requestId ?? item.RequestId ?? null,
            requestStatus: statusName(item.requestStatus !== undefined ? item.requestStatus : item.RequestStatus),
            statusText: item.statusText ?? item.StatusText ?? null,
            originalTitle: item.originalTitle ?? item.OriginalTitle ?? null,
            network: item.network ?? item.Network ?? null,
            studio: item.studio ?? item.Studio ?? null,
            originalLanguage: item.originalLanguage ?? item.OriginalLanguage ?? null,
            seasonCount: item.seasonCount ?? item.SeasonCount ?? null,
            episodeCount: item.episodeCount ?? item.EpisodeCount ?? null,
            seriesStatus: item.seriesStatus ?? item.SeriesStatus ?? null,
            genres: item.genres ?? item.Genres ?? [],
            runtimeMinutes: item.runtimeMinutes ?? item.RuntimeMinutes ?? null,
            externalLinks: (item.externalLinks ?? item.ExternalLinks ?? []).map(l => ({
                name: l.name ?? l.Name ?? '',
                url: l.url ?? l.Url ?? ''
            })),
            missingEpisodes: item.missingEpisodes ?? item.MissingEpisodes ?? null,
            isAnime: Boolean(item.isAnime ?? item.IsAnime)
        };
    }

    function normalizeCatalogResponse(payload) {
        if (!payload || typeof payload !== 'object') {
            return {
                items: [],
                diagnosticMessage: null,
                isPartial: false,
                sonarr: null,
                radarr: null
            };
        }
        if (Array.isArray(payload)) {
            return {
                items: payload.map(normalizeCatalogItem),
                diagnosticMessage: null,
                isPartial: false,
                sonarr: null,
                radarr: null
            };
        }
        const rawItems = payload.items || payload.Items || [];
        const items = Array.isArray(rawItems) ? rawItems.map(normalizeCatalogItem) : [];
        const diagnosticMessage = payload.diagnosticMessage ?? payload.DiagnosticMessage ?? null;
        const isPartial = Boolean(payload.isPartial ?? payload.IsPartial);
        const sonarr = payload.sonarr || payload.Sonarr || null;
        const radarr = payload.radarr || payload.Radarr || null;

        return {
            items,
            diagnosticMessage,
            isPartial,
            sonarr: sonarr ? {
                status: sonarr.status ?? sonarr.Status,
                message: sonarr.message ?? sonarr.Message,
                isConfigured: Boolean(sonarr.isConfigured ?? sonarr.IsConfigured)
            } : null,
            radarr: radarr ? {
                status: radarr.status ?? radarr.Status,
                message: radarr.message ?? radarr.Message,
                isConfigured: Boolean(radarr.isConfigured ?? radarr.IsConfigured)
            } : null,
            totalRemoteFound: payload.totalRemoteFound ?? payload.TotalRemoteFound ?? items.length,
            localMatchesCount: payload.localMatchesCount ?? payload.LocalMatchesCount ?? 0
        };
    }

    // ---- Spanish labels for TMDB/Sonarr metadata (unknown values pass through unchanged) ----
    const LANGUAGE_NAMES = {
        english: 'Inglés', japanese: 'Japonés', spanish: 'Español', korean: 'Coreano', french: 'Francés',
        german: 'Alemán', italian: 'Italiano', chinese: 'Chino', mandarin: 'Mandarín', cantonese: 'Cantonés',
        portuguese: 'Portugués', russian: 'Ruso', hindi: 'Hindi', thai: 'Tailandés', turkish: 'Turco',
        arabic: 'Árabe', dutch: 'Neerlandés', swedish: 'Sueco', norwegian: 'Noruego', danish: 'Danés',
        finnish: 'Finés', polish: 'Polaco', czech: 'Checo', hungarian: 'Húngaro', greek: 'Griego',
        hebrew: 'Hebreo', ukrainian: 'Ucraniano', indonesian: 'Indonesio', vietnamese: 'Vietnamita',
        tagalog: 'Tagalo', filipino: 'Filipino', catalan: 'Catalán', romanian: 'Rumano', persian: 'Persa',
        tamil: 'Tamil', telugu: 'Telugu', bengali: 'Bengalí', malay: 'Malayo', icelandic: 'Islandés'
    };

    const GENRE_NAMES = {
        'action': 'Acción', 'adventure': 'Aventura', 'animation': 'Animación', 'anime': 'Anime', 'comedy': 'Comedia',
        'crime': 'Crimen', 'documentary': 'Documental', 'drama': 'Drama', 'family': 'Familiar', 'fantasy': 'Fantasía',
        'history': 'Historia', 'horror': 'Terror', 'music': 'Música', 'musical': 'Musical', 'mystery': 'Misterio',
        'romance': 'Romance', 'science fiction': 'Ciencia ficción', 'sci-fi': 'Ciencia ficción',
        'sci-fi & fantasy': 'Ciencia ficción y fantasía', 'action & adventure': 'Acción y aventura',
        'thriller': 'Suspense', 'war': 'Bélica', 'western': 'Wéstern', 'reality': 'Telerrealidad',
        'talk': 'Entrevistas', 'kids': 'Infantil', 'children': 'Infantil', 'news': 'Noticias', 'soap': 'Telenovela',
        'war & politics': 'Bélica y política', 'sports': 'Deportes', 'sport': 'Deporte', 'game show': 'Concurso',
        'tv movie': 'Película de TV', 'television movie': 'Película de TV', 'mini-series': 'Miniserie',
        'suspense': 'Suspense', 'indie': 'Independiente', 'food': 'Gastronomía', 'travel': 'Viajes',
        'home and garden': 'Hogar y jardín', 'special interest': 'Interés especial'
    };

    function translateLabel(map, value) {
        const text = String(value ?? '').trim();
        return map[text.toLowerCase()] || text;
    }

    function translateLanguage(value) {
        return translateLabel(LANGUAGE_NAMES, value);
    }

    function translateGenre(value) {
        return translateLabel(GENRE_NAMES, value);
    }

    // ---- "Tus solicitudes" ----
    function normalizeRequestSummary(raw) {
        const r = raw || {};
        const p = r.progress || r.Progress || {};
        const providerIds = r.providerIds || r.ProviderIds || {};
        return {
            id: r.id ?? r.Id ?? '',
            kind: kindName(r.kind !== undefined ? r.kind : r.Kind),
            title: r.title ?? r.Title ?? '',
            year: r.year !== undefined ? r.year : r.Year,
            posterUrl: r.posterUrl ?? r.PosterUrl ?? '',
            providerIds: {
                tvdb: providerIds.tvdb ?? providerIds.Tvdb ?? null,
                tmdb: providerIds.tmdb ?? providerIds.Tmdb ?? null,
                imdb: providerIds.imdb ?? providerIds.Imdb ?? null
            },
            status: statusName(r.status !== undefined ? r.status : r.Status),
            needsAction: Boolean(r.needsAction ?? r.NeedsAction),
            requestedBy: r.requestedBy ?? r.RequestedBy ?? null,
            progress: {
                total: Number(p.total ?? p.Total ?? 0) || 0,
                available: Number(p.available ?? p.Available ?? 0) || 0,
                attention: Number(p.attention ?? p.Attention ?? 0) || 0,
                awaitingSelection: Boolean(p.awaitingSelection ?? p.AwaitingSelection),
                following: Boolean(p.following ?? p.Following),
                waitingForAir: p.waitingForAir ?? p.WaitingForAir ?? null
            }
        };
    }

    // Short line under the title of a request card
    function requestMetaText(r) {
        const p = r.progress;
        let text;
        if (p.awaitingSelection) text = 'Elige una release';
        else if (p.attention > 0 || r.status === 'RequiresAttention') text = 'Requiere atención';
        else if (p.waitingForAir) text = r.kind === 'Movie' ? 'Esperando estreno' : 'Esperando emisión';
        else if (p.total > 1) text = `${p.available} de ${p.total} disponibles`;
        else if (r.status === 'Available') text = p.following ? 'Siguiendo la emisión' : 'Disponible';
        else text = STATUS_LABELS[r.status] || '';
        if (!text) text = r.kind === 'Movie' ? 'Película' : 'Serie';
        if (p.following && p.total > 1) text += ' · Siguiendo';
        if (r.requestedBy) text += ` · ${r.requestedBy}`;
        return text;
    }

    // Catalog-like item so a request card can reuse createItemCard and openDetailsModal (which opens the status view)
    function requestToItem(r) {
        return {
            id: r.id,
            kind: r.kind,
            title: r.title,
            year: r.year,
            posterUrl: r.posterUrl,
            providerIds: r.providerIds,
            requestId: r.id,
            requestStatus: r.status
        };
    }

    function init() {
        document.addEventListener('input', (e) => {
            const target = e.target;
            if (target && (target.type === 'search' || target.classList.contains('searchFields') || target.id?.toLowerCase().includes('search'))) {
                activeSearchInput = target;
                onSearchInput(target.value);
            }
        });

        document.addEventListener('keydown', (e) => {
            const target = e.target;
            if (e.key === 'Enter' && target && (target.type === 'search' || target.classList.contains('searchFields') || target.id?.toLowerCase().includes('search'))) {
                activeSearchInput = target;
                onSearchNow(target.value);
            }
        });

        window.addEventListener('hashchange', () => {
            lastQuery = '';
            if (currentAbortController) {
                currentAbortController.abort();
                currentAbortController = null;
            }
            setTimeout(checkPageContext, 300);
        });

        window.addEventListener('popstate', () => {
            lastQuery = '';
            if (currentAbortController) {
                currentAbortController.abort();
                currentAbortController = null;
            }
            setTimeout(checkPageContext, 300);
        });

        const observer = new MutationObserver(() => {
            checkPageContext();
        });
        observer.observe(document.body, { childList: true, subtree: true });

        checkPageContext();
    }

    function checkPageContext() {
        keepSectionInPlace();
        maintainRequestsSection();
        ensureHeaderButton();
        // The count only exists for a logged-in user
        if (hasSession()) startAttentionPolling();
        checkSeriesDetailsPage();
    }

    function isSearchRoute() {
        return typeof window !== 'undefined' && window.location && /^#!?\/search/.test(window.location.hash || '');
    }

    // Keeps the external block after the local results: React appends late local results behind it and may
    // remove it when it re-renders the page (e.g. when the query is written to the URL)
    function keepSectionInPlace() {
        if (!currentSection || !isSearchRoute()) return;
        if (!activeSearchInput || !activeSearchInput.isConnected) {
            activeSearchInput = document.querySelector('#searchTextInput, .searchfields-txtSearch, input[type=search]');
        }
        if (!activeSearchInput || (activeSearchInput.value || '').trim() !== renderedQuery) return;

        const container = getSearchContainer();
        if (container && (currentSection.parentElement !== container || container.lastElementChild !== currentSection)) {
            container.appendChild(currentSection);
        }
    }

    // Short titles ("Up", "It") search automatically from 2 characters; Enter searches at once even with 1
    const MIN_AUTO_SEARCH_LENGTH = 2;

    function onSearchNow(val) {
        clearTimeout(debounceTimer);
        const query = (val || '').trim();
        if (query.length > 0) {
            performExternalSearch(query);
        }
    }

    function onSearchInput(val) {
        clearTimeout(debounceTimer);
        const query = (val || '').trim();

        // Typing replaces "Tus solicitudes"; clearing the box brings it back (maintainRequestsSection)
        if (query.length > 0 && sectionMode === 'requests') {
            removeExternalSection();
        }

        if (query.length < MIN_AUTO_SEARCH_LENGTH) {
            if (currentAbortController) {
                currentAbortController.abort();
                currentAbortController = null;
            }
            removeExternalSection();
            lastQuery = '';
            return;
        }

        debounceTimer = setTimeout(() => {
            performExternalSearch(query);
        }, 350);
    }

    async function performExternalSearch(query) {
        if (query === lastQuery) return;
        lastQuery = query;

        if (currentAbortController) {
            currentAbortController.abort();
        }
        currentAbortController = new AbortController();
        const signal = currentAbortController.signal;

        renderedQuery = query;
        sectionMode = 'external';
        try {
            renderLoadingIndicator();
            const apiBase = getApiBase();
            const response = await fetch(`${apiBase}/Acquisition/catalog?query=${encodeURIComponent(query)}`, {
                headers: getAuthHeaders(),
                signal
            });

            if (!response.ok) {
                renderExternalResults([], query, { diagnosticMessage: `Error HTTP ${response.status} en la búsqueda externa.` });
                return;
            }

            const payload = await response.json();
            const normalized = normalizeCatalogResponse(payload);
            renderExternalResults(normalized.items, query, normalized);
        } catch (err) {
            if (err.name !== 'AbortError') {
                console.error('[Acquisition] Error searching external catalog:', err);
                renderExternalResults([], query, { diagnosticMessage: 'No se pudo conectar con el catálogo externo.' });
            }
        }
    }

    function getSearchContainer() {
        // Jellyfin Web 12.x renders the search page inside an animated page positioned over a zero-height wrapper:
        // appending next to that wrapper (e.g. in <main>) paints the section on top of the local results.
        // The page that holds the search field keeps the section in the normal flow, after the local results.
        const searchPage = activeSearchInput && activeSearchInput.isConnected && activeSearchInput.closest
            ? activeSearchInput.closest('.mainAnimatedPage, .page')
            : null;
        return searchPage ||
               document.querySelector('.searchView .searchResults') ||
               document.querySelector('[data-testid="search-results"]') ||
               document.querySelector('.searchResults') ||
               document.querySelector('.view:not(.hide) .content-primary') ||
               document.querySelector('.page:not(.hide) .content-primary') ||
               document.querySelector('main');
    }

    function removeExternalSection() {
        const el = document.getElementById('acquisition-search-section');
        if (el) el.remove();
        currentSection = null;
        renderedQuery = '';
        sectionMode = '';
    }

    function findSearchInput() {
        if (activeSearchInput && activeSearchInput.isConnected) return activeSearchInput;
        return document.querySelector('#searchTextInput, .searchfields-txtSearch, input[type=search]');
    }

    // Shows "Tus solicitudes" on the search page while the search box is empty
    function maintainRequestsSection() {
        if (!isSearchRoute()) {
            // The search page (and the section with it) is gone; a stale reference must not block the next visit
            if (currentSection) removeExternalSection();
            return;
        }
        if (requestsLoading || Date.now() < requestsRetryAfter) return;

        const input = findSearchInput();
        if (!input || (input.value || '').trim().length > 0) return;
        if (sectionMode === 'requests' && currentSection && currentSection.isConnected) return;
        // Results of a previous query with the box now empty (cleared without an input event)
        if (sectionMode === 'external') removeExternalSection();

        activeSearchInput = input;
        loadRequestsSection();
    }

    function isRequestsSectionVisible() {
        return sectionMode === 'requests' && currentSection && currentSection.isConnected;
    }

    async function fetchRequestSummaries() {
        const resp = await fetch(`${getApiBase()}/Acquisition/requests`, { headers: getAuthHeaders() });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const data = await resp.json();
        return (Array.isArray(data) ? data : []).map(normalizeRequestSummary);
    }

    async function loadRequestsSection() {
        const container = getSearchContainer();
        if (!container) return;
        requestsLoading = true;
        try {
            const rows = await fetchRequestSummaries();
            const input = findSearchInput();
            // The user may have typed or left the page while the list was loading
            if (!isSearchRoute() || sectionMode === 'external' || !input || (input.value || '').trim().length > 0) return;
            const target = getSearchContainer();
            if (!target) return;
            sectionMode = 'requests';
            renderedQuery = '';
            renderRequestsSection(ensureSectionAttached(target), rows);
        } catch (err) {
            // No list is better than a broken search page; try again in a while
            console.warn('[Acquisition] Could not load requests:', err);
            requestsRetryAfter = Date.now() + 30000;
        } finally {
            requestsLoading = false;
        }
    }

    function renderRequestsSection(section, rows) {
        const needing = rows.filter(r => r.needsAction).length;
        const subtitle = needing > 0
            ? `${rows.length} solicitud${rows.length === 1 ? '' : 'es'} • ${needing} necesita${needing === 1 ? '' : 'n'} tu atención`
            : rows.length > 0 ? `${rows.length} solicitud${rows.length === 1 ? '' : 'es'}` : '';
        section.innerHTML = `
            <div class="acquisition-header">
                <h2>Tus solicitudes</h2>
                ${subtitle ? `<span class="acquisition-subtitle">${escapeHtml(subtitle)}</span>` : ''}
            </div>
            ${rows.length === 0
                ? '<div class="acquisition-empty"><p class="acquisition-empty-text">No tienes solicitudes. Busca una película o serie para pedirla.</p></div>'
                : '<div class="acquisition-cards-grid" id="acquisition-requests-grid"></div>'}
        `;

        const grid = section.querySelector('#acquisition-requests-grid');
        if (!grid) return;
        rows.forEach(r => {
            const card = createItemCard(requestToItem(r), { metaText: requestMetaText(r) });
            if (r.needsAction) card.classList.add('needs-action');
            grid.appendChild(card);
        });
    }

    // ---- Header button ("Mis solicitudes") with the number of requests that need the user's action ----
    const COUNT_POLL_MS = 60000;
    const LAST_COUNT_KEY = 'acquisition.lastAttentionCount';
    const TOAST_MS = 8000;
    let attentionTimer = null;
    let toastTimer = null;

    const REQUESTS_ICON_SVG = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor" aria-hidden="true" focusable="false">' +
        '<path d="M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H5V5h14v14zM7 7h2v2H7V7zm4 0h6v2h-6V7zm-4 4h2v2H7v-2zm4 0h6v2h-6v-2zm-4 4h2v2H7v-2zm4 0h6v2h-6v-2z"/></svg>';

    function hasSession() {
        const api = typeof window !== 'undefined' ? window.ApiClient : null;
        return Boolean(api && api.accessToken && api.accessToken());
    }

    // Jellyfin Web 10.9+ (React) puts the search button in the app bar (an <a> to the search route inside .MuiToolbar-root);
    // the legacy header (.headerRight with .headerSearchButton) is still in the DOM but hidden there, so only a visible
    // candidate counts. The button goes right before the search button.
    function isShown(el) {
        if (!el || !el.getClientRects || el.getClientRects().length === 0) return false;
        const rect = el.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0;
    }

    function findHeaderSearchButton() {
        const candidates = document.querySelectorAll(
            '.MuiAppBar-root a[href*="search"], .MuiToolbar-root a[href*="search"], .headerSearchButton, .skinHeader a[href*="search"], header a[href*="search"], ' +
            '.MuiAppBar-root button[aria-label="Search"], .MuiAppBar-root button[aria-label="Buscar"], .skinHeader button[aria-label="Search"], .skinHeader button[aria-label="Buscar"]');
        return Array.from(candidates).find(isShown) || null;
    }

    function ensureHeaderButton() {
        if (!hasSession()) return;
        const searchButton = findHeaderSearchButton();
        const existing = document.getElementById('acquisition-requests-button');
        if (existing) {
            // Left in a header that is now hidden (e.g. the legacy one): move it next to the visible search button
            if (isShown(existing) || !searchButton) return;
            existing.remove();
        }

        const headerRight = searchButton ? null : Array.from(document.querySelectorAll('.headerRight')).find(isShown);
        if (!searchButton && !headerRight) return;

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.id = 'acquisition-requests-button';
        btn.className = themeClassName('acquisition-header-btn paper-icon-button-light');
        btn.title = 'Mis solicitudes';
        btn.setAttribute('aria-label', 'Mis solicitudes');
        btn.innerHTML = `${REQUESTS_ICON_SVG}<span class="acquisition-header-badge" style="display: none;"></span>`;
        btn.addEventListener('click', goToRequests);

        if (searchButton) {
            searchButton.parentElement.insertBefore(btn, searchButton);
        } else {
            headerRight.insertBefore(btn, headerRight.firstChild);
        }
        refreshAttentionCount();
    }

    function goToRequests() {
        dismissToast();
        if (isSearchRoute()) {
            // Already there: show the list again if the search box is empty
            requestsRetryAfter = 0;
            maintainRequestsSection();
            if (isRequestsSectionVisible()) loadRequestsSection();
        } else {
            window.location.hash = '#/search';
        }
    }

    function setBadge(count) {
        const badge = document.querySelector('#acquisition-requests-button .acquisition-header-badge');
        if (!badge) return;
        badge.textContent = count > 99 ? '99+' : String(count);
        badge.style.display = count > 0 ? '' : 'none';
        const btn = document.getElementById('acquisition-requests-button');
        if (btn) btn.title = count > 0 ? `Mis solicitudes (${count} necesitan tu atención)` : 'Mis solicitudes';
    }

    function readLastCount() {
        try {
            const raw = window.sessionStorage.getItem(LAST_COUNT_KEY);
            const n = raw === null ? 0 : parseInt(raw, 10);
            return Number.isFinite(n) ? n : 0;
        } catch (err) {
            return 0;
        }
    }

    function writeLastCount(count) {
        try {
            window.sessionStorage.setItem(LAST_COUNT_KEY, String(count));
        } catch (err) {
            // Without storage the toast may repeat after a reload; nothing else depends on it
        }
    }

    async function refreshAttentionCount() {
        if (!hasSession() || document.visibilityState === 'hidden') return;
        try {
            const resp = await fetch(`${getApiBase()}/Acquisition/requests/attention-count`, { headers: getAuthHeaders() });
            if (!resp.ok) return;
            const data = await resp.json();
            const count = Math.max(0, Number(data.count ?? data.Count ?? 0) || 0);
            setBadge(count);
            const last = readLastCount();
            writeLastCount(count);
            if (count > last) showAttentionToast(count);
        } catch (err) {
            // The badge keeps its last value
        }
    }

    function startAttentionPolling() {
        if (attentionTimer) return;
        attentionTimer = setInterval(refreshAttentionCount, COUNT_POLL_MS);
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') refreshAttentionCount();
        });
        // Anything done in the modal (choosing a release, cancelling, requesting) can change the count
        document.addEventListener('acquisition-modal-closed', () => {
            refreshAttentionCount();
            if (isRequestsSectionVisible()) loadRequestsSection();
        });
    }

    function dismissToast() {
        clearTimeout(toastTimer);
        const el = document.getElementById('acquisition-toast');
        if (el) el.remove();
    }

    function showAttentionToast(count) {
        dismissToast();
        const toast = document.createElement('div');
        toast.id = 'acquisition-toast';
        toast.className = themeClassName('acquisition-toast');
        toast.setAttribute('role', 'status');
        toast.textContent = count === 1
            ? 'Tienes 1 solicitud que necesita tu atención'
            : `Tienes ${count} solicitudes que necesitan tu atención`;
        toast.addEventListener('click', goToRequests);
        document.body.appendChild(toast);
        toastTimer = setTimeout(dismissToast, TOAST_MS);
    }

    function ensureSectionAttached(container) {
        let section = document.getElementById('acquisition-search-section') || currentSection;
        if (!section) {
            section = document.createElement('div');
            section.id = 'acquisition-search-section';
            section.className = themeClassName('acquisition-section');
            container.appendChild(section);
        } else if (container && section.parentElement !== container) {
            container.appendChild(section);
        } else if (container && container.lastElementChild !== section) {
            container.appendChild(section);
        }
        currentSection = section;
        return section;
    }

    function renderLoadingIndicator() {
        const container = getSearchContainer();
        if (!container) return;
        const section = ensureSectionAttached(container);
        section.innerHTML = `
            <div class="acquisition-header">
                <h2>Fuera de tu biblioteca</h2>
            </div>
            <div class="acquisition-loading">Buscando en catálogo externo...</div>
        `;
    }

    function renderExternalResults(items, query, payload) {
        const container = getSearchContainer();
        if (!container) return;
        const section = ensureSectionAttached(container);

        // Normalize items and payload defensively so PascalCase or camelCase works seamlessly
        let normalizedPayload = null;
        if (payload) {
            normalizedPayload = normalizeCatalogResponse(payload);
        }

        let normalizedItems = [];
        if (Array.isArray(items) && items.length > 0) {
            normalizedItems = items.map(normalizeCatalogItem);
        } else if (normalizedPayload && normalizedPayload.items && normalizedPayload.items.length > 0) {
            normalizedItems = normalizedPayload.items;
        }

        if (normalizedItems.length === 0) {
            const rawDiagnostic = (payload && (payload.diagnosticMessage ?? payload.DiagnosticMessage))
                || (normalizedPayload && normalizedPayload.diagnosticMessage);
            const msg = rawDiagnostic || 'Sin coincidencias externas.';

            const sonarr = (normalizedPayload && normalizedPayload.sonarr) || (payload && (payload.sonarr || payload.Sonarr));
            const radarr = (normalizedPayload && normalizedPayload.radarr) || (payload && (payload.radarr || payload.Radarr));
            const sonarrStatus = sonarr ? (sonarr.status ?? sonarr.Status) : null;
            const radarrStatus = radarr ? (radarr.status ?? radarr.Status) : null;
            const isNotConfigured = sonarrStatus === 'NotConfigured' && radarrStatus === 'NotConfigured';

            section.innerHTML = `
                <div class="acquisition-header">
                    <h2>Fuera de tu biblioteca</h2>
                </div>
                <div class="acquisition-empty">
                    <p style="margin: 0 0 10px 0; color: #bbb;">${escapeHtml(msg)}</p>
                    ${isNotConfigured ? '<p><a href="#!/configurationpage?name=Jellyfind" style="color: #00a4dc; text-decoration: underline;">Configurar catálogo externo en el panel de administración</a></p>' : ''}
                    <button id="btn-retry-catalog" class="acquisition-btn-secondary" style="margin-top: 10px;">Reintentar búsqueda</button>
                </div>
            `;

            const retryBtn = section.querySelector('#btn-retry-catalog');
            if (retryBtn) {
                retryBtn.addEventListener('click', () => {
                    lastQuery = '';
                    performExternalSearch(query);
                });
            }
            return;
        }

        const isPartial = Boolean((normalizedPayload && normalizedPayload.isPartial) || (payload && (payload.isPartial ?? payload.IsPartial)));
        const diagMsg = (normalizedPayload && normalizedPayload.diagnosticMessage) || (payload && (payload.diagnosticMessage ?? payload.DiagnosticMessage));
        const subtitle = (diagMsg && isPartial)
            ? `${normalizedItems.length} títulos encontrados • <span style="color: #ff9800;">${escapeHtml(diagMsg)}</span>`
            : `${normalizedItems.length} títulos encontrados`;

        section.innerHTML = `
            <div class="acquisition-header">
                <h2>Fuera de tu biblioteca</h2>
                <span class="acquisition-subtitle">${subtitle}</span>
            </div>
            <div class="acquisition-cards-grid" id="acquisition-grid"></div>
        `;

        const grid = section.querySelector('#acquisition-grid');
        normalizedItems.forEach(item => {
            const card = createItemCard(item);
            grid.appendChild(card);
        });
    }

    function createItemCard(rawItem, options) {
        const item = normalizeCatalogItem(rawItem);
        const card = document.createElement('div');
        card.className = 'acquisition-card';

        const posterSrc = resolvePosterSrc(item);
        const kindLabel = item.kind === 'Movie' ? 'Película' : 'Serie';
        const yearLabel = item.year ? `(${item.year})` : '';

        // An active request badge wins over the "in library" one
        const hasRequest = item.requestStatus !== null && item.requestStatus !== undefined;
        const isLocalSeries = item.isLocal && item.kind === 'Series';
        let badgeHtml = '';
        if (hasRequest) {
            const label = STATUS_LABELS[item.requestStatus] || item.statusText || 'Solicitado';
            badgeHtml = `<span class="acquisition-badge status-${escapeHtml(String(item.requestStatus))}">${escapeHtml(label)}</span>`;
        } else if (isLocalSeries) {
            badgeHtml = '<span class="acquisition-badge status-InLibrary">En biblioteca</span>';
        }

        let metaText = `${kindLabel} ${yearLabel}`.trim();
        if (isLocalSeries && !hasRequest && item.missingEpisodes > 0) {
            metaText += item.missingEpisodes === 1 ? ' · Falta 1 episodio' : ` · Faltan ${item.missingEpisodes} episodios`;
        }

        if (options && options.metaText) metaText = options.metaText;

        card.innerHTML = `
            <div class="acquisition-card-image-box">
                ${posterHtml(item, posterSrc, 'acquisition-card-image')}
                ${badgeHtml}
            </div>
            <div class="acquisition-card-content">
                <div class="acquisition-card-title">${escapeHtml(item.title)}</div>
                <div class="acquisition-card-meta">${escapeHtml(metaText)}</div>
            </div>
        `;

        bindPosterFallback(card);
        card.addEventListener('click', () => {
            if (isLocalSeries && !hasRequest) {
                // Same flow as the button on the series detail page
                openLocalSeriesRequest({
                    title: item.title,
                    year: item.year,
                    tvdb: item.providerIds.tvdb,
                    tmdb: item.providerIds.tmdb,
                    imdb: item.providerIds.imdb
                });
            } else if (item.requestId && !item.overview) {
                openRequestWithDetails(item, card);
            } else {
                openDetailsModal(item);
            }
        });

        return card;
    }

    // Cards of "Tus solicitudes" only carry what the request stores: the description, poster and facts come from the catalog
    async function openRequestWithDetails(item, card) {
        const providerId = item.kind === 'Series' ? item.providerIds.tvdb : item.providerIds.tmdb;
        let merged = item;
        if (providerId && !card.dataset.loading) {
            card.dataset.loading = '1';
            try {
                const resp = await fetch(`${getApiBase()}/Acquisition/catalog/${encodeURIComponent(item.kind)}/${encodeURIComponent(providerId)}`, { headers: getAuthHeaders() });
                if (resp.ok) {
                    const details = normalizeCatalogItem(await resp.json());
                    merged = Object.assign({}, details, {
                        requestId: item.requestId,
                        requestStatus: item.requestStatus,
                        posterUrl: details.posterUrl || item.posterUrl
                    });
                }
            } catch (err) {
                console.warn('[Acquisition] Could not load title details:', err);
            } finally {
                delete card.dataset.loading;
            }
        }
        openDetailsModal(merged);
    }

    function resolvePosterSrc(item) {
        if (!item.posterUrl) return '';
        return item.posterUrl.startsWith('/') ? `${getApiBase()}${item.posterUrl}` : item.posterUrl;
    }

    // A poster that is missing or fails to load is replaced by the kind icon and the title, like Jellyfin's own cards
    function posterHtml(item, src, imgClass) {
        const icon = item.kind === 'Movie' ? 'movie' : 'tv';
        const fallback = `<div class="acquisition-poster-fallback"${src ? ' style="display: none;"' : ''}>` +
            `<span class="material-icons ${icon}" aria-hidden="true"></span><span>${escapeHtml(item.title)}</span></div>`;
        const img = src ? `<img class="${imgClass}" src="${escapeHtml(src)}" alt="${escapeHtml(item.title)}" loading="lazy" />` : '';
        return img + fallback;
    }

    function bindPosterFallback(root) {
        root.querySelectorAll('img').forEach(img => img.addEventListener('error', () => {
            const fallback = img.parentElement && img.parentElement.querySelector('.acquisition-poster-fallback');
            if (!fallback) return;
            img.remove();
            fallback.style.display = '';
        }));
    }

    async function readErrorMessage(resp, fallback) {
        try {
            const data = await resp.json();
            return (data && (data.message || data.Message)) || fallback;
        } catch (err) {
            return fallback;
        }
    }

    function closeModal(modal) {
        // A series request created only to list episodes and never confirmed must not stay blocking the title
        const orphanId = modal.dataset.unconfirmedRequestId;
        if (orphanId) {
            fetch(`${getApiBase()}/Acquisition/requests/${encodeURIComponent(orphanId)}`, { method: 'DELETE', headers: getAuthHeaders() })
                .catch(err => console.warn('[Acquisition] Could not cancel unconfirmed request:', err));
        }
        modal.dispatchEvent(new Event('acquisition-closed'));
        modal.remove();
        document.dispatchEvent(new Event('acquisition-modal-closed'));
    }

    function isUnconfirmedSeriesScope(req) {
        const scope = req.scope || req.Scope || {};
        const list = (a, b) => (scope[a] || scope[b] || []);
        return statusName(req.status ?? req.Status) === 'Pending' &&
            kindName(req.kind ?? req.Kind) === 'Series' &&
            !(scope.all ?? scope.All) &&
            list('targetEpisodes', 'TargetEpisodes').length === 0 &&
            list('episodeIds', 'EpisodeIds').length === 0 &&
            list('seasonNumbers', 'SeasonNumbers').length === 0;
    }

    const SERIES_STATUS_LABELS = { continuing: 'En emisión', ended: 'Finalizada', upcoming: 'Próximamente' };
    const LOW_SPACE_BYTES = 20 * 1024 * 1024 * 1024;

    function formatBytes(bytes) {
        const gb = bytes / (1024 * 1024 * 1024);
        if (gb >= 1024) return `${(gb / 1024).toFixed(1)} TB`;
        return `${Math.max(0, Math.round(gb))} GB`;
    }

    // Compact facts under the title that tell apart works sharing a name, plus links to the provider pages
    function detailsHtml(item) {
        const facts = [];
        if (item.originalTitle) facts.push(`Título original: ${item.originalTitle}`);
        if (item.network) facts.push(item.network);
        if (item.studio) facts.push(item.studio);
        if (item.originalLanguage) facts.push(translateLanguage(item.originalLanguage));
        if (item.seasonCount > 0) facts.push(item.seasonCount === 1 ? '1 temporada' : `${item.seasonCount} temporadas`);
        if (item.episodeCount > 0) facts.push(item.episodeCount === 1 ? '1 episodio' : `${item.episodeCount} episodios`);
        if (item.seriesStatus && SERIES_STATUS_LABELS[item.seriesStatus]) facts.push(SERIES_STATUS_LABELS[item.seriesStatus]);
        if (item.runtimeMinutes > 0) facts.push(`${item.runtimeMinutes} min`);
        if (Array.isArray(item.genres) && item.genres.length > 0) facts.push(item.genres.slice(0, 4).map(translateGenre).join(', '));

        const links = (item.externalLinks || []).filter(l => l.name && /^https:\/\//.test(l.url || ''));
        let html = '';
        if (facts.length > 0) {
            html += `<div class="acquisition-modal-facts">${facts.map(escapeHtml).join(' · ')}</div>`;
        }
        if (links.length > 0) {
            html += `<div class="acquisition-modal-links">${links.map(l =>
                `<a href="${escapeHtml(l.url)}" target="_blank" rel="noopener noreferrer">${escapeHtml(l.name)}</a>`).join(' · ')}</div>`;
        }
        return html;
    }

    async function openDetailsModal(rawItem) {
        const item = normalizeCatalogItem(rawItem);
        const modal = document.createElement('div');
        modal.className = themeClassName('acquisition-modal-overlay');
        modal.id = 'acquisition-modal';

        const apiBase = getApiBase();
        const kindLabel = item.kind === 'Movie' ? 'Película' : 'Serie';
        const yearLabel = item.year ? `(${item.year})` : '';
        const posterSrc = resolvePosterSrc(item);

        modal.innerHTML = `
            <div class="acquisition-modal-content" role="dialog" aria-modal="true" aria-label="${escapeHtml(item.title)}">
                <button type="button" class="acquisition-modal-close" id="modal-close" aria-label="Cerrar" title="Cerrar">&times;</button>
                <div class="acquisition-modal-body">
                    <div class="acquisition-modal-sidebar">
                        <div class="acquisition-modal-poster-box">${posterHtml(item, posterSrc, 'acquisition-modal-poster')}</div>
                    </div>
                    <div class="acquisition-modal-main">
                        <h2 class="acquisition-modal-title">${escapeHtml(item.title)} <span class="year">${yearLabel}</span></h2>
                        <div class="acquisition-modal-meta">${kindLabel}</div>
                        ${detailsHtml(item)}
                        <p class="acquisition-modal-overview">${escapeHtml(item.overview || 'Sin descripción disponible.')}</p>

                        <div id="acquisition-destination-area" class="acquisition-destination-area" style="display: none; margin-bottom: 16px;">
                            <label for="acquisition-destination-select" class="acquisition-field-label">Destino</label>
                            <select id="acquisition-destination-select" class="acquisition-select"></select>
                            <p id="acquisition-destination-hint" class="acquisition-field-hint" style="display: none;">Sugerido: parece anime</p>
                            <p id="acquisition-free-space" class="acquisition-field-hint" style="display: none;"></p>
                        </div>
                        <p id="acquisition-request-error" class="acquisition-error" style="display: none;"></p>

                        <div id="acquisition-action-area" class="acquisition-action-area">
                            <button id="btn-add-library" class="acquisition-btn-primary">Añadir a biblioteca</button>
                        </div>

                        <div id="acquisition-scope-area" class="acquisition-scope-area" style="display: none;">
                            <h3>Seleccionar contenido a solicitar</h3>
                            <div class="scope-options">
                                <label><input type="radio" name="scopeType" value="all" /> Serie completa</label>
                                <label><input type="radio" name="scopeType" value="custom" /> Seleccionar episodios</label>
                            </div>
                            <p id="scope-all-summary" class="acquisition-scope-summary" style="display: none;"></p>
                            <div id="episodes-container" class="episodes-checklist" style="display: none;">
                                <em>Cargando episodios...</em>
                            </div>
                            <div class="scope-actions">
                                <button id="btn-confirm-scope" class="acquisition-btn-primary" disabled>Confirmar y solicitar</button>
                                <button id="btn-back-status" class="acquisition-btn-secondary" style="display: none;">Volver</button>
                            </div>
                        </div>

                        <div id="acquisition-status-area" class="acquisition-status-area" style="display: none;">
                            <div class="status-box">
                                <span class="status-label">Estado:</span>
                                <strong id="status-text">-</strong>
                            </div>
                            <p id="status-detail"></p>
                            <div id="status-progress" class="acquisition-progress" style="display: none;"></div>
                            <div class="status-actions">
                                <button id="btn-view-local" class="acquisition-btn-primary" style="display: none;">Ver en biblioteca</button>
                                <button id="btn-view-releases" class="acquisition-btn-secondary" style="display: none;">Seleccionar release</button>
                                <button id="btn-retry" class="acquisition-btn-secondary" style="display: none;">Reintentar</button>
                                <button id="btn-skip-parked" class="acquisition-btn-secondary" style="display: none;" title="Deja de intentar los que fallan; podrás volver a pedirlos más tarde">Omitir apartados</button>
                                <button id="btn-add-episodes" class="acquisition-btn-secondary" style="display: none;">Añadir temporadas o episodios</button>
                                <button id="btn-cancel-request" class="acquisition-btn-secondary" style="display: none;">Cancelar solicitud</button>
                            </div>
                            <div id="cancel-confirm" class="acquisition-cancel-confirm" style="display: none;"></div>
                            <div id="releases-container" class="releases-selection-list" style="display: none;"></div>
                        </div>
                    </div>
                </div>
            </div>
        `;

        document.body.appendChild(modal);
        bindPosterFallback(modal);
        // Season/episode counts for the "Serie completa" summary, also when the picker is cloned from the status view
        modal.acquisitionItem = item;

        const onKeyDown = (e) => {
            if (e.key === 'Escape' && document.body.contains(modal)) closeModal(modal);
        };
        document.addEventListener('keydown', onKeyDown);
        modal.addEventListener('acquisition-closed', () => document.removeEventListener('keydown', onKeyDown));
        modal.querySelector('#modal-close').addEventListener('click', () => closeModal(modal));
        modal.addEventListener('click', (e) => {
            if (e.target === modal) closeModal(modal);
        });

        const errorEl = modal.querySelector('#acquisition-request-error');
        const showError = (msg) => {
            errorEl.textContent = msg || '';
            errorEl.style.display = msg ? 'block' : 'none';
        };

        if (item.requestId) {
            // A series request whose episodes were never chosen goes back to the episode picker instead of a dead status view
            try {
                const resp = await fetch(`${apiBase}/Acquisition/requests/${encodeURIComponent(item.requestId)}`, { headers: getAuthHeaders() });
                if (resp.ok && isUnconfirmedSeriesScope(await resp.json())) {
                    modal.querySelector('#acquisition-action-area').style.display = 'none';
                    modal.querySelector('#acquisition-scope-area').style.display = 'block';
                    await setupSeriesScopePicker(modal, item, () => ({ id: null }), showError, item.requestId);
                    return;
                }
            } catch (err) {
                console.warn('[Acquisition] Could not load existing request:', err);
            }
            showStatusView(modal, item.requestId);
            return;
        }

        const destArea = modal.querySelector('#acquisition-destination-area');
        const destSelect = modal.querySelector('#acquisition-destination-select');
        const btnAdd = modal.querySelector('#btn-add-library');

        try {
            const optResp = await fetch(`${apiBase}/Acquisition/request-options?kind=${encodeURIComponent(item.kind)}`, { headers: getAuthHeaders() });
            if (optResp.status === 403) {
                btnAdd.disabled = true;
                showError('No tienes permiso para solicitar contenido.');
            } else if (optResp.ok) {
                const optData = await optResp.json();
                const destinations = optData.destinations || optData.Destinations || [];
                const defaultId = optData.defaultDestinationId ?? optData.DefaultDestinationId ?? null;
                const optError = optData.errorMessage ?? optData.ErrorMessage ?? null;
                if (optError) {
                    btnAdd.disabled = true;
                    showError(optError);
                } else if (destinations.length > 0) {
                    const destId = d => d.id ?? d.Id ?? '';
                    const isAnimeDest = d => String(d.seriesType ?? d.SeriesType ?? '').toLowerCase() === 'anime';
                    let selectedId = defaultId;
                    let suggestedAnimeId = null;
                    if (item.kind === 'Series') {
                        const animeDests = destinations.filter(isAnimeDest);
                        if (item.isAnime && animeDests.length > 0) {
                            const preferred = animeDests.find(d => destId(d) === defaultId) || animeDests[0];
                            selectedId = suggestedAnimeId = destId(preferred);
                        } else if (!item.isAnime) {
                            // Never default a non-anime series into an anime destination
                            const def = destinations.find(d => destId(d) === defaultId);
                            if (def && isAnimeDest(def)) {
                                const others = destinations.filter(d => !isAnimeDest(d));
                                selectedId = others.length === 1 ? destId(others[0]) : null;
                            }
                        }
                    }

                    // Without a single option or an explicit default the user must choose; nothing is picked silently
                    const placeholder = selectedId ? '' : '<option value="" selected disabled>Elige un destino…</option>';
                    destSelect.innerHTML = placeholder + destinations.map(d => {
                        const id = destId(d);
                        const name = d.name ?? d.Name ?? '';
                        const seriesType = d.seriesType ?? d.SeriesType ?? '';
                        const rootFolderPath = d.rootFolderPath ?? d.RootFolderPath ?? '';
                        const typeSuffix = seriesType ? ` (${escapeHtml(seriesType)})` : '';
                        const pathSuffix = rootFolderPath ? ` - ${escapeHtml(rootFolderPath)}` : '';
                        const selected = selectedId && id === selectedId ? ' selected' : '';
                        return `<option value="${escapeHtml(id)}"${selected}>${escapeHtml(name)}${typeSuffix}${pathSuffix}</option>`;
                    }).join('');

                    const freeById = {};
                    destinations.forEach(d => { freeById[destId(d)] = d.freeSpaceBytes ?? d.FreeSpaceBytes ?? null; });
                    const hintEl = modal.querySelector('#acquisition-destination-hint');
                    const freeEl = modal.querySelector('#acquisition-free-space');
                    const refreshDestinationInfo = () => {
                        hintEl.style.display = suggestedAnimeId && destSelect.value === suggestedAnimeId ? 'block' : 'none';
                        const free = freeById[destSelect.value];
                        if (typeof free === 'number' && free >= 0) {
                            const low = free < LOW_SPACE_BYTES;
                            freeEl.textContent = `Espacio libre: ${formatBytes(free)}` + (low ? '. Queda poco espacio en el servidor; avisa al administrador.' : '');
                            freeEl.classList.toggle('low-space', low);
                            freeEl.style.display = 'block';
                        } else {
                            freeEl.style.display = 'none';
                        }
                    };
                    destSelect.addEventListener('change', refreshDestinationInfo);
                    refreshDestinationInfo();
                    destArea.style.display = 'block';
                }
            } else {
                showError(await readErrorMessage(optResp, `No se pudieron cargar los destinos (HTTP ${optResp.status}).`));
            }
        } catch (err) {
            console.warn('[Acquisition] Could not load request options:', err);
            showError('No se pudieron cargar los destinos.');
        }

        const getSelectedDestination = () => ({
            id: destArea.style.display !== 'none' && destSelect.value ? destSelect.value : null
        });

        btnAdd.addEventListener('click', async () => {
            // R01: Capture selection before hiding the destination area
            const destInfo = getSelectedDestination();
            if (destArea.style.display !== 'none' && !destInfo.id) {
                showError('Elige un destino antes de continuar.');
                return;
            }
            showError('');

            if (item.kind === 'Series') {
                modal.querySelector('#acquisition-action-area').style.display = 'none';
                destArea.style.display = 'none';
                modal.querySelector('#acquisition-scope-area').style.display = 'block';
                await setupSeriesScopePicker(modal, item, () => destInfo, showError, null);
            } else {
                btnAdd.disabled = true;
                btnAdd.textContent = 'Solicitando...';
                const created = await createAcquisitionRequest(item, { all: true }, destInfo.id);
                if (created.id) {
                    destArea.style.display = 'none';
                    showStatusView(modal, created.id);
                } else {
                    btnAdd.disabled = false;
                    btnAdd.textContent = 'Reintentar solicitud';
                    showError(created.error);
                }
            }
        });
    }

    // What "Serie completa" will request; counts that are unknown are left out
    function allScopeSummary(item) {
        const seasons = Number(item && item.seasonCount) || 0;
        const episodes = Number(item && item.episodeCount) || 0;
        const episodesText = episodes === 1 ? '1 episodio ya emitido' : `${episodes} episodios ya emitidos`;
        let text;
        if (seasons > 0) {
            text = `${seasons === 1 ? 'Se pedirá 1 temporada' : `Se pedirán ${seasons} temporadas`}${episodes > 0 ? ` (${episodesText})` : ''}`;
        } else if (episodes > 0) {
            text = `${episodes === 1 ? 'Se pedirá' : 'Se pedirán'} ${episodesText}`;
        } else {
            text = 'Se pedirá la serie completa';
        }
        const ended = String(item && item.seriesStatus || '').toLowerCase() === 'ended';
        return ended ? `${text}.` : `${text} y se descargarán los episodios nuevos según se emitan.`;
    }

    function updateAllScopeSummary(root, item) {
        const summary = root.querySelector('#scope-all-summary');
        if (!summary) return;
        const checked = root.querySelector('input[name="scopeType"]:checked');
        const show = Boolean(checked && checked.value === 'all');
        summary.textContent = show ? allScopeSummary(item) : '';
        summary.style.display = show ? 'block' : 'none';
    }

    async function setupSeriesScopePicker(modal, item, getSelectedDestination, showError, existingRequestId) {
        const episodesContainer = modal.querySelector('#episodes-container');
        const radios = modal.querySelectorAll('input[name="scopeType"]');
        let createdRequest = existingRequestId ? { id: existingRequestId } : null;
        const getDestInfo = typeof getSelectedDestination === 'function' ? getSelectedDestination : () => ({ id: null });

        const btnConfirmScope = modal.querySelector('#btn-confirm-scope');
        // Nothing is preselected: one stray click must not request a whole series and follow it while it airs
        btnConfirmScope.disabled = !modal.querySelector('input[name="scopeType"]:checked');

        const markUnconfirmed = (id) => { modal.dataset.unconfirmedRequestId = id; };
        const markConfirmed = () => { delete modal.dataset.unconfirmedRequestId; };

        const loadEpisodes = async () => {
            episodesContainer.innerHTML = '<em>Preparando serie y cargando episodios...</em>';
            const prepared = await prepareSeriesInArr(createdRequest.id);
            if (prepared.ok) {
                loadEpisodesChecklist(modal, createdRequest.id);
            } else {
                episodesContainer.innerHTML = `<em>${escapeHtml(prepared.error)}</em>`;
            }
        };

        if (existingRequestId) {
            markUnconfirmed(existingRequestId);
            modal.querySelector('input[name="scopeType"][value="custom"]').checked = true;
            btnConfirmScope.disabled = false;
            episodesContainer.style.display = 'block';
            await loadEpisodes();
        }

        radios.forEach(r => {
            r.addEventListener('change', async (e) => {
                btnConfirmScope.disabled = false;
                updateAllScopeSummary(modal, item);
                if (e.target.value === 'custom') {
                    episodesContainer.style.display = 'block';
                    if (!createdRequest) {
                        const created = await createAcquisitionRequest(item, { all: false }, getDestInfo().id);
                        if (created.id && created.existing) {
                            // The series already has a request in progress: the choice is added to it
                            createdRequest = created;
                            loadEpisodesChecklist(modal, created.id);
                        } else if (created.id) {
                            createdRequest = created;
                            markUnconfirmed(created.id);
                            await loadEpisodes();
                        } else {
                            episodesContainer.innerHTML = `<em>${escapeHtml(created.error)}</em>`;
                        }
                    }
                } else {
                    episodesContainer.style.display = 'none';
                }
            });
        });

        const btnConfirm = modal.querySelector('#btn-confirm-scope');
        btnConfirm.addEventListener('click', async () => {
            btnConfirm.disabled = true;
            btnConfirm.textContent = 'Confirmando...';
            const fail = (msg) => {
                btnConfirm.disabled = false;
                btnConfirm.textContent = 'Error al confirmar. Reintentar';
                if (showError) showError(msg);
            };

            const chosenScope = modal.querySelector('input[name="scopeType"]:checked');
            if (!chosenScope) {
                fail('Elige «Serie completa» o «Seleccionar episodios».');
                return;
            }

            const scopeType = chosenScope.value;
            if (scopeType === 'all') {
                // Created right here with the whole series already in its scope: nothing left to confirm
                let createdWithAll = false;
                if (!createdRequest) {
                    const created = await createAcquisitionRequest(item, { all: true }, getDestInfo().id);
                    if (!created.id) {
                        fail(created.error);
                        return;
                    }
                    createdRequest = created;
                    createdWithAll = !created.existing;
                }

                if (createdRequest.existing) {
                    const added = await addToRequest(createdRequest.id, { all: true, seasonNumbers: [], episodeIds: [] });
                    if (!added.ok) {
                        fail(added.error);
                        return;
                    }
                } else if (!createdWithAll && !await confirmScope(createdRequest.id, { all: true, seasonNumbers: [], episodeNumbers: [], episodeIds: [] })) {
                    fail('No se pudo confirmar la serie completa.');
                    return;
                }
                markConfirmed();
                showStatusView(modal, createdRequest.id);
            } else {
                const checked = Array.from(modal.querySelectorAll('.chk-episode:checked:not(:disabled)'));
                const checkedSeasons = Array.from(modal.querySelectorAll('.chk-season:checked')).map(cb => parseInt(cb.value, 10));
                if (!createdRequest || (checked.length === 0 && checkedSeasons.length === 0)) {
                    fail('Selecciona al menos un episodio o una temporada.');
                    return;
                }

                if (createdRequest.existing) {
                    const added = await addToRequest(createdRequest.id, {
                        all: false,
                        seasonNumbers: checkedSeasons,
                        episodeIds: checked.map(cb => parseInt(cb.dataset.id, 10)).filter(Boolean)
                    });
                    if (added.ok) {
                        showStatusView(modal, createdRequest.id);
                    } else {
                        fail(added.error);
                    }
                    return;
                }

                const confirmed = await confirmScope(createdRequest.id, {
                    all: false,
                    seasonNumbers: checkedSeasons,
                    episodeNumbers: checked.map(cb => parseInt(cb.value, 10)),
                    episodeIds: checked.map(cb => parseInt(cb.dataset.id, 10)).filter(Boolean),
                    targetEpisodes: checked.map(cb => ({
                        episodeId: parseInt(cb.dataset.id, 10),
                        seasonNumber: parseInt(cb.dataset.season, 10),
                        episodeNumber: parseInt(cb.value, 10)
                    }))
                });
                if (confirmed) {
                    markConfirmed();
                    showStatusView(modal, createdRequest.id);
                } else {
                    fail('No se pudo confirmar la selección.');
                }
            }
        });
    }

    async function prepareSeriesInArr(requestId) {
        const apiBase = getApiBase();
        try {
            const resp = await fetch(`${apiBase}/Acquisition/requests/${encodeURIComponent(requestId)}/prepare`, {
                method: 'POST',
                headers: getAuthHeaders()
            });
            if (resp.ok) return { ok: true };
            return { ok: false, error: await readErrorMessage(resp, `No se pudo preparar la serie en Sonarr (HTTP ${resp.status}).`) };
        } catch (err) {
            console.error('[Acquisition] PrepareSeries error:', err);
            return { ok: false, error: 'Error de conexión al preparar la serie.' };
        }
    }

    // Right after a series is added, Sonarr fills its episode list in the background for a few seconds
    const EPISODE_LOAD_ATTEMPTS = 10;
    const EPISODE_LOAD_RETRY_MS = 3000;

    async function loadEpisodesChecklist(modal, requestId, attempt = 1) {
        const container = modal.querySelector('#episodes-container');
        const apiBase = getApiBase();
        try {
            const resp = await fetch(`${apiBase}/Acquisition/requests/${encodeURIComponent(requestId)}/episodes`, { headers: getAuthHeaders() });
            if (!resp.ok) {
                container.innerHTML = '<em>Error al cargar episodios (código ' + resp.status + ').</em>';
                return;
            }
            const rawEpisodes = await resp.json();
            const episodes = (Array.isArray(rawEpisodes) ? rawEpisodes : []).map(e => ({
                id: e.id ?? e.Id,
                seasonNumber: e.seasonNumber ?? e.SeasonNumber,
                episodeNumber: e.episodeNumber ?? e.EpisodeNumber,
                title: e.title ?? e.Title ?? '',
                hasFile: Boolean(e.hasFile ?? e.HasFile),
                requested: Boolean(e.isSelected ?? e.IsSelected)
            }));
            if (episodes.length === 0) {
                if (attempt < EPISODE_LOAD_ATTEMPTS && document.body.contains(modal)) {
                    container.innerHTML = '<em>Sonarr está cargando los episodios de la serie...</em>';
                    setTimeout(() => loadEpisodesChecklist(modal, requestId, attempt + 1), EPISODE_LOAD_RETRY_MS);
                    return;
                }
                container.innerHTML = '<em>Sonarr no ha devuelto episodios para esta serie.</em>';
                return;
            }

            const seasons = {};
            episodes.forEach(e => {
                if (!seasons[e.seasonNumber]) seasons[e.seasonNumber] = [];
                seasons[e.seasonNumber].push(e);
            });

            // Specials (season 0) go last; with a single regular season it starts expanded
            const seasonNumbers = Object.keys(seasons).map(Number).sort((a, b) => (a === 0) - (b === 0) || a - b);
            const regularSeasons = seasonNumbers.filter(n => n !== 0).length;
            let html = '';
            for (const sNum of seasonNumbers) {
                const list = seasons[sNum].sort((a, b) => a.episodeNumber - b.episodeNumber);
                const owned = list.filter(ep => ep.hasFile || ep.requested).length;
                const countText = owned
                    ? `${list.length} episodios · ${owned} ya en biblioteca o solicitados`
                    : `${list.length} episodio${list.length === 1 ? '' : 's'}`;
                const allOwned = owned === list.length;
                html += `
                    <details class="season-block"${sNum !== 0 && regularSeasons === 1 ? ' open' : ''}>
                        <summary class="season-title">
                            <input type="checkbox" class="chk-season" value="${sNum}"${allOwned ? ' disabled' : ''} aria-label="Seleccionar ${sNum === 0 ? 'especiales' : `temporada ${sNum}`}" />
                            <strong>${sNum === 0 ? 'Especiales' : `Temporada ${sNum}`}</strong>
                            <span class="season-count">${countText}</span>
                        </summary>
                        <div class="episodes-sublist">
                `;
                list.forEach(ep => {
                    const note = ep.hasFile ? 'Ya en biblioteca' : ep.requested ? 'Ya solicitado' : '';
                    html += `
                        <label class="episode-item ${ep.hasFile ? 'has-file' : ''}">
                            <input type="checkbox" class="chk-episode" value="${ep.episodeNumber}" data-id="${ep.id}" data-season="${sNum}" ${ep.hasFile || ep.requested ? 'disabled checked' : ''} />
                            <span class="episode-number">Ep. ${ep.episodeNumber}</span>
                            <span>${escapeHtml(ep.title)}${note ? ` <span class="episode-note">· ${note}</span>` : ''}</span>
                        </label>
                    `;
                });
                html += `</div></details>`;
            }

            container.innerHTML = html;

            container.querySelectorAll('.chk-season').forEach(sBox => {
                // Ticking the season inside the summary must not fold or unfold it
                sBox.addEventListener('click', (e) => e.stopPropagation());
                sBox.addEventListener('change', (e) => {
                    const season = e.target.value;
                    container.querySelectorAll(`.chk-episode[data-season="${season}"]:not(:disabled)`).forEach(epBox => {
                        epBox.checked = e.target.checked;
                    });
                });
            });
        } catch (err) {
            container.innerHTML = '<em>Error de conexión al cargar episodios.</em>';
        }
    }

    async function createAcquisitionRequest(item, scope, destinationId) {
        const apiBase = getApiBase();
        try {
            // Only a stable destination ID is sent: the server resolves the real folder, profile and series type
            const payload = {
                kind: item.kind,
                title: item.title,
                year: item.year,
                providerIds: item.providerIds,
                scope: scope,
                destinationId: destinationId || null,
                // Opaque image path of the catalog item; the server validates it before storing it for "Tus solicitudes"
                posterUrl: item.posterUrl || null
            };

            const resp = await fetch(`${apiBase}/Acquisition/requests`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify(payload)
            });

            if (resp.status === 409) {
                const data = await resp.json();
                return { id: data.requestId || data.RequestId, existing: true };
            }

            if (!resp.ok) {
                return { id: null, error: await readErrorMessage(resp, `No se pudo crear la solicitud (HTTP ${resp.status}).`) };
            }
            const created = await resp.json();
            return {
                id: created.id || created.Id,
                status: statusName(created.status !== undefined ? created.status : created.Status),
                statusText: created.statusText || created.StatusText
            };
        } catch (err) {
            console.error('[Acquisition] CreateRequest error:', err);
            return { id: null, error: 'Error de conexión al crear la solicitud.' };
        }
    }

    // Adds seasons or episodes to a series request already in progress; returns { ok, error }
    async function addToRequest(requestId, scopeData) {
        try {
            const resp = await fetch(`${getApiBase()}/Acquisition/requests/${encodeURIComponent(requestId)}/add`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify(scopeData)
            });
            return resp.ok ? { ok: true } : { ok: false, error: await readErrorMessage(resp, 'No se pudo añadir a la solicitud.') };
        } catch (err) {
            console.error('[Acquisition] AddToRequest error:', err);
            return { ok: false, error: 'Error de conexión al añadir a la solicitud.' };
        }
    }

    async function confirmScope(requestId, scopeData) {
        const apiBase = getApiBase();
        try {
            const resp = await fetch(`${apiBase}/Acquisition/requests/${encodeURIComponent(requestId)}/confirm`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify(scopeData)
            });
            return resp.ok;
        } catch (err) {
            console.error('[Acquisition] ConfirmScope error:', err);
            return false;
        }
    }

    function showStatusView(modal, requestId) {
        modal.querySelector('#acquisition-action-area').style.display = 'none';
        modal.querySelector('#acquisition-scope-area').style.display = 'none';
        const statusArea = modal.querySelector('#acquisition-status-area');
        statusArea.style.display = 'block';

        const statusText = modal.querySelector('#status-text');
        const statusDetail = modal.querySelector('#status-detail');
        const statusProgress = modal.querySelector('#status-progress');
        const btnRetry = modal.querySelector('#btn-retry');
        const btnViewLocal = modal.querySelector('#btn-view-local');
        const btnViewReleases = modal.querySelector('#btn-view-releases');
        const btnCancel = modal.querySelector('#btn-cancel-request');
        const releasesContainer = modal.querySelector('#releases-container');
        const btnAddEpisodes = modal.querySelector('#btn-add-episodes');
        const btnSkipParked = modal.querySelector('#btn-skip-parked');
        const cancelConfirm = modal.querySelector('#cancel-confirm');
        const requestUrl = `${getApiBase()}/Acquisition/requests/${encodeURIComponent(requestId)}`;
        let stopped = false;
        // Last state of the request read by poll(): decides what the cancel panel and the auto-select box offer
        let lastReq = null;

        // The releases panel = automatic-selection box on top + the list of releases (the list is rewritten as it loads)
        releasesContainer.innerHTML = '';
        const autoBox = document.createElement('div');
        autoBox.className = 'acquisition-auto-select';
        autoBox.style.display = 'none';
        const releasesList = document.createElement('div');
        releasesContainer.append(autoBox, releasesList);

        const showNotice = (msg) => {
            const notice = modal.querySelector('#acquisition-request-error');
            notice.textContent = msg || '';
            notice.style.display = msg ? 'block' : 'none';
        };

        // Manual mode only: offer to let the plugin choose the releases of this request from now on
        function renderAutoBox() {
            if (!lastReq || !lastReq.manualReleaseMode) {
                autoBox.style.display = 'none';
                return;
            }
            autoBox.style.display = 'block';
            if (lastReq.autoReleaseSelection) {
                autoBox.innerHTML = '<span>Selección automática activada</span> ' +
                    '<button type="button" class="acquisition-link-btn" id="btn-auto-select-off">Desactivar</button>';
            } else {
                autoBox.innerHTML = '<button type="button" class="acquisition-btn-secondary" id="btn-auto-select-on">Elegir automáticamente a partir de ahora</button>' +
                    '<p class="acquisition-field-hint">El plugin elegirá la mejor release para este y los siguientes episodios.</p>';
            }
            const toggle = autoBox.querySelector('button');
            toggle.addEventListener('click', async () => {
                const enabled = !lastReq.autoReleaseSelection;
                toggle.disabled = true;
                try {
                    const resp = await fetch(`${requestUrl}/auto-select`, {
                        method: 'POST',
                        headers: getAuthHeaders(),
                        body: JSON.stringify({ enabled })
                    });
                    if (resp.ok) {
                        showNotice('');
                        lastReq.autoReleaseSelection = enabled;
                        renderAutoBox();
                        poll();
                    } else {
                        toggle.disabled = false;
                        showNotice(await readErrorMessage(resp, 'No se pudo cambiar la selección automática.'));
                    }
                } catch (err) {
                    toggle.disabled = false;
                    showNotice('Error de conexión al cambiar la selección automática.');
                }
            });
        }

        btnSkipParked.addEventListener('click', async () => {
            btnSkipParked.disabled = true;
            try {
                const resp = await fetch(`${requestUrl}/skip-parked`, { method: 'POST', headers: getAuthHeaders() });
                showNotice(resp.ok ? '' : await readErrorMessage(resp, 'No se pudieron omitir las descargas apartadas.'));
            } catch (err) {
                showNotice('Error de conexión al omitir las descargas apartadas.');
            }
            btnSkipParked.disabled = false;
            poll();
        });

        btnAddEpisodes.addEventListener('click', () => {
            // A fresh copy of the picker: listeners left by the creation flow must not run here
            const oldArea = modal.querySelector('#acquisition-scope-area');
            const scopeArea = oldArea.cloneNode(true);
            oldArea.replaceWith(scopeArea);
            scopeArea.querySelector('h3').textContent = 'Añadir a la solicitud';
            const container = scopeArea.querySelector('#episodes-container');
            const btnConfirm = scopeArea.querySelector('#btn-confirm-scope');
            const btnBack = scopeArea.querySelector('#btn-back-status');
            btnConfirm.disabled = false;
            btnConfirm.textContent = 'Añadir a la solicitud';
            btnBack.style.display = 'inline-block';
            scopeArea.querySelector('input[name="scopeType"][value="custom"]').checked = true;
            container.style.display = 'block';
            scopeArea.querySelectorAll('input[name="scopeType"]').forEach(r => r.addEventListener('change', (e) => {
                container.style.display = e.target.value === 'custom' ? 'block' : 'none';
                updateAllScopeSummary(scopeArea, modal.acquisitionItem);
            }));
            updateAllScopeSummary(scopeArea, modal.acquisitionItem);

            const backToStatus = () => {
                scopeArea.style.display = 'none';
                statusArea.style.display = 'block';
            };
            btnBack.addEventListener('click', backToStatus);
            btnConfirm.addEventListener('click', async () => {
                const all = scopeArea.querySelector('input[name="scopeType"]:checked').value === 'all';
                const checked = Array.from(container.querySelectorAll('.chk-episode:checked:not(:disabled)'));
                const checkedSeasons = Array.from(container.querySelectorAll('.chk-season:checked')).map(cb => parseInt(cb.value, 10));
                const notice = modal.querySelector('#acquisition-request-error');
                if (!all && checked.length === 0 && checkedSeasons.length === 0) {
                    notice.textContent = 'Selecciona al menos un episodio o una temporada.';
                    notice.style.display = 'block';
                    return;
                }

                btnConfirm.disabled = true;
                btnConfirm.textContent = 'Añadiendo...';
                const added = await addToRequest(requestId, {
                    all: all,
                    seasonNumbers: all ? [] : checkedSeasons,
                    episodeIds: all ? [] : checked.map(cb => parseInt(cb.dataset.id, 10)).filter(Boolean)
                });
                btnConfirm.disabled = false;
                btnConfirm.textContent = 'Añadir a la solicitud';
                if (added.ok) {
                    notice.style.display = 'none';
                    backToStatus();
                    poll();
                } else {
                    notice.textContent = added.error;
                    notice.style.display = 'block';
                }
            });

            statusArea.style.display = 'none';
            scopeArea.style.display = 'block';
            container.innerHTML = '<em>Cargando episodios...</em>';
            loadEpisodesChecklist(modal, requestId);
        });

        btnRetry.addEventListener('click', async () => {
            btnRetry.disabled = true;
            await fetch(`${requestUrl}/retry`, { method: 'POST', headers: getAuthHeaders() });
            poll();
        });

        // In-modal confirmation instead of window.confirm; "Dejar de seguir" has nothing to delete, so no checkbox
        btnCancel.addEventListener('click', () => {
            const following = Boolean(lastReq && lastReq.status === 'Available');
            cancelConfirm.innerHTML = following
                ? '<p class="acquisition-cancel-title">¿Dejar de seguir esta serie?</p>' +
                  '<p class="acquisition-field-hint">Los episodios ya descargados se conservan; los nuevos dejarán de pedirse.</p>'
                : '<p class="acquisition-cancel-title">¿Cancelar esta solicitud?</p>' +
                  '<label class="acquisition-cancel-option"><input type="checkbox" id="chk-delete-downloads" checked /> ' +
                  'Borrar también lo que se está descargando y aún no está en tu biblioteca</label>';
            cancelConfirm.innerHTML += '<div class="acquisition-cancel-actions">' +
                `<button type="button" class="acquisition-btn-primary" id="btn-cancel-yes">${following ? 'Sí, dejar de seguir' : 'Sí, cancelar'}</button>` +
                '<button type="button" class="acquisition-btn-secondary" id="btn-cancel-no">No</button></div>';
            cancelConfirm.style.display = 'block';
            btnCancel.disabled = true;

            cancelConfirm.querySelector('#btn-cancel-no').addEventListener('click', () => {
                cancelConfirm.style.display = 'none';
                btnCancel.disabled = false;
            });
            cancelConfirm.querySelector('#btn-cancel-yes').addEventListener('click', async (e) => {
                const deleteBox = cancelConfirm.querySelector('#chk-delete-downloads');
                const deleteDownloads = Boolean(deleteBox && deleteBox.checked);
                e.target.disabled = true;
                const resp = await fetch(deleteDownloads ? `${requestUrl}?deleteDownloads=true` : requestUrl, { method: 'DELETE', headers: getAuthHeaders() });
                if (resp.ok) {
                    stopped = true;
                    statusText.textContent = 'Cancelada';
                    statusDetail.textContent = 'La solicitud se ha cancelado.';
                    [btnRetry, btnSkipParked, btnViewReleases, btnViewLocal, btnAddEpisodes, btnCancel].forEach(b => { b.style.display = 'none'; });
                    cancelConfirm.style.display = 'none';
                    releasesContainer.style.display = 'none';
                } else {
                    e.target.disabled = false;
                    cancelConfirm.style.display = 'none';
                    btnCancel.disabled = false;
                    statusDetail.textContent = await readErrorMessage(resp, 'No se pudo cancelar la solicitud.');
                }
            });
        });

        btnViewReleases.addEventListener('click', async () => {
            if (releasesContainer.style.display === 'block') {
                releasesContainer.style.display = 'none';
                btnViewReleases.textContent = 'Seleccionar release';
                return;
            }

            releasesContainer.style.display = 'block';
            renderAutoBox();
            releasesList.innerHTML = '<em>Cargando releases candidatas...</em>';
            btnViewReleases.textContent = 'Ocultar releases';

            try {
                const resp = await fetch(`${requestUrl}/releases`, { headers: getAuthHeaders() });
                if (!resp.ok) {
                    releasesList.innerHTML = `<em>${escapeHtml(await readErrorMessage(resp, 'Error al cargar releases.'))}</em>`;
                    return;
                }
                const rawReleases = await resp.json();
                const releases = (Array.isArray(rawReleases) ? rawReleases : []).map(rel => ({
                    id: rel.id ?? rel.Id,
                    title: rel.title ?? rel.Title ?? '',
                    resolution: rel.resolution ?? rel.Resolution,
                    source: rel.source ?? rel.Source,
                    codec: rel.codec ?? rel.Codec,
                    seeders: rel.seeders ?? rel.Seeders ?? 0,
                    size: rel.size ?? rel.Size ?? 0,
                    isEligible: Boolean(rel.isEligible ?? rel.IsEligible),
                    rejectionReasons: rel.rejectionReasons || rel.RejectionReasons || []
                }));
                if (releases.length === 0) {
                    releasesList.innerHTML = '<em>No se encontraron releases disponibles.</em>';
                    return;
                }

                const rowHtml = (rel) => `
                        <div class="release-row ${rel.isEligible ? 'eligible' : 'ineligible'}">
                            <div class="release-info">
                                <strong>${escapeHtml(rel.title)}</strong>
                                <div class="release-meta">
                                    <span>${escapeHtml(rel.resolution || 'Res: ?')}</span> •
                                    <span>${escapeHtml(rel.source || 'Src: ?')}</span> •
                                    <span>${escapeHtml(rel.codec || 'Codec: ?')}</span> •
                                    <span>Seeders: ${Number(rel.seeders) || 0}</span> •
                                    <span>${((Number(rel.size) || 0) / 1073741824).toFixed(2)} GB</span>
                                </div>
                                ${rel.rejectionReasons?.length ? `<div class="rejection-reasons">${rel.rejectionReasons.map(r => `<span>${escapeHtml(r)}</span>`).join('')}</div>` : ''}
                            </div>
                            <div class="release-action">
                                ${rel.isEligible ? `<button class="acquisition-btn-secondary btn-select-release" data-id="${escapeHtml(rel.id)}">Elegir</button>` : '<span class="label-ineligible">No elegible</span>'}
                            </div>
                        </div>
                    `;
                // The ones that can be chosen come first; the discarded ones stay folded with their reasons
                const eligible = releases.filter(r => r.isEligible);
                const discarded = releases.filter(r => !r.isEligible);
                let html = eligible.length
                    ? `<div class="releases-table">${eligible.map(rowHtml).join('')}</div>`
                    : '<em>Ninguna release encontrada es válida para este episodio o película.</em>';
                if (discarded.length) {
                    html += `<details class="releases-discarded"${eligible.length ? '' : ' open'}>` +
                        `<summary>${discarded.length} descartada${discarded.length === 1 ? '' : 's'} (ver motivos)</summary>` +
                        `<div class="releases-table">${discarded.map(rowHtml).join('')}</div></details>`;
                }
                releasesList.innerHTML = html;

                releasesList.querySelectorAll('.btn-select-release').forEach(btn => {
                    btn.addEventListener('click', async (e) => {
                        const relId = e.target.dataset.id;
                        btn.disabled = true;
                        btn.textContent = 'Seleccionando...';
                        const selResp = await fetch(`${requestUrl}/release`, {
                            method: 'POST',
                            headers: getAuthHeaders(),
                            body: JSON.stringify({ releaseId: relId })
                        });
                        if (selResp.ok) {
                            releasesContainer.style.display = 'none';
                            btnViewReleases.textContent = 'Seleccionar release';
                            poll();
                        } else {
                            // Show why (e.g. the tracker already sent another release) and refresh the real state
                            btn.disabled = false;
                            btn.textContent = 'Elegir';
                            const notice = modal.querySelector('#acquisition-request-error');
                            notice.textContent = await readErrorMessage(selResp, 'No se pudo seleccionar la release.');
                            notice.style.display = 'block';
                            releasesContainer.style.display = 'none';
                            btnViewReleases.textContent = 'Seleccionar release';
                            poll();
                        }
                    });
                });
            } catch (err) {
                releasesList.innerHTML = '<em>Error de conexión al cargar releases.</em>';
            }
        });

        async function poll() {
            if (stopped || !document.getElementById('acquisition-modal')) return;
            try {
                const resp = await fetch(requestUrl, { headers: getAuthHeaders() });
                if (resp.status === 404) {
                    statusText.textContent = 'No encontrada';
                    statusDetail.textContent = 'La solicitud ya no existe.';
                    return;
                }
                if (resp.ok) {
                    const reqRaw = await resp.json();
                    const scope = reqRaw.scope || reqRaw.Scope || {};
                    const req = {
                        kind: kindName(reqRaw.kind ?? reqRaw.Kind),
                        wholeSeries: Boolean(scope.all ?? scope.All),
                        status: statusName(reqRaw.status !== undefined ? reqRaw.status : reqRaw.Status),
                        attentionReason: reqRaw.attentionReason ?? reqRaw.AttentionReason,
                        errorMessage: reqRaw.errorMessage ?? reqRaw.ErrorMessage,
                        localItemId: reqRaw.localItemId ?? reqRaw.LocalItemId,
                        progress: reqRaw.progress || reqRaw.Progress || null,
                        nextAttemptUtc: reqRaw.nextAttemptUtc ?? reqRaw.NextAttemptUtc ?? null,
                        autoReleaseSelection: Boolean(reqRaw.autoReleaseSelection ?? reqRaw.AutoReleaseSelection),
                        manualReleaseMode: Boolean(reqRaw.manualReleaseMode ?? reqRaw.ManualReleaseMode),
                        history: (reqRaw.history || reqRaw.History || []).map(h => ({
                            message: h.message ?? h.Message,
                            status: statusName(h.status !== undefined ? h.status : h.Status)
                        }))
                    };
                    statusText.textContent = STATUS_LABELS[req.status] || req.status || 'Procesando';
                    statusDetail.textContent = req.attentionReason || req.errorMessage || (req.history?.length ? req.history[req.history.length - 1].message : '');
                    lastReq = req;
                    // Only while the request is searching: the next automatic search is a known time
                    const progress = renderProgress(statusProgress, req.progress, req.status === 'Searching' ? req.nextAttemptUtc : null);
                    if (releasesContainer.style.display === 'block') renderAutoBox();

                    if (req.status === 'Available' && req.localItemId) {
                        btnViewLocal.style.display = 'inline-block';
                        btnViewLocal.onclick = () => {
                            window.location.hash = `#!/details?id=${encodeURIComponent(req.localItemId)}`;
                            closeModal(modal);
                        };
                    } else {
                        btnViewLocal.style.display = 'none';
                    }

                    // Searching / Selecting releases (including RequiresAttention - K01)
                    if (req.status === 'Searching' || req.status === 'Pending' || req.status === 'RequiresAttention') {
                        btnViewReleases.style.display = 'inline-block';
                    } else {
                        btnViewReleases.style.display = 'none';
                        releasesContainer.style.display = 'none';
                    }

                    // Downloads set aside can be requeued even while the rest of the request keeps going
                    if (req.status === 'RequiresAttention' || req.status === 'Failed' || progress.attention > 0) {
                        btnRetry.style.display = 'inline-block';
                        btnRetry.disabled = false;
                    } else {
                        btnRetry.style.display = 'none';
                    }
                    btnSkipParked.style.display = progress.attention > 0 ? 'inline-block' : 'none';

                    // One request per series: more seasons or episodes are added to the one in progress
                    const active = req.status !== 'Failed' && (req.status !== 'Available' || progress.following);
                    btnAddEpisodes.style.display = req.kind === 'Series' && active && !(req.wholeSeries && progress.following) ? 'inline-block' : 'none';

                    btnCancel.style.display = req.status === 'Available' && !progress.following ? 'none' : 'inline-block';
                    btnCancel.textContent = req.status === 'Available' && progress.following ? 'Dejar de seguir' : 'Cancelar solicitud';

                    if (req.status !== 'Available' && req.status !== 'Failed') {
                        setTimeout(poll, 5000);
                    }
                }
            } catch (err) {
                console.error('[Acquisition] Poll error:', err);
                setTimeout(poll, 8000);
            }
        }

        poll();
    }

    // "hoy a las 18:40" / "mañana a las 09:00" / "12/03 a las 09:00" in local time; null when the time is unknown or past
    function formatNextAttempt(value, now = new Date()) {
        if (!value) return null;
        const date = new Date(value);
        if (Number.isNaN(date.getTime()) || date.getTime() <= now.getTime()) return null;
        const pad = (n) => String(n).padStart(2, '0');
        const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
        const startOfDay = (d) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
        const days = Math.round((startOfDay(date) - startOfDay(now)) / 86400000);
        if (days === 0) return `hoy a las ${time}`;
        if (days === 1) return `mañana a las ${time}`;
        return `${pad(date.getDate())}/${pad(date.getMonth() + 1)} a las ${time}`;
    }

    // Summary of a request's downloads: counts, the one in progress, what needs attention and a manual-selection hint
    function renderProgress(container, raw, nextAttemptUtc) {
        const p = raw || {};
        const num = (a, b) => Number(p[a] ?? p[b] ?? 0) || 0;
        const progress = {
            total: num('total', 'Total'),
            available: num('available', 'Available'),
            inProgress: num('inProgress', 'InProgress'),
            pending: num('pending', 'Pending'),
            attention: num('attention', 'Attention'),
            skipped: num('skipped', 'Skipped'),
            skippedItems: p.skippedItems || p.SkippedItems || [],
            current: p.current ?? p.Current ?? null,
            items: p.attentionItems || p.AttentionItems || [],
            awaitingSelection: Boolean(p.awaitingSelection ?? p.AwaitingSelection),
            following: Boolean(p.following ?? p.Following),
            waitingForAir: p.waitingForAir ?? p.WaitingForAir ?? null
        };

        const parts = [];
        if (progress.total > 1) {
            const pieces = [`${progress.available} de ${progress.total} disponibles`];
            if (progress.inProgress) pieces.push(`${progress.inProgress} en curso${progress.current ? ` (${escapeHtml(progress.current)})` : ''}`);
            if (progress.pending) pieces.push(`${progress.pending} pendiente${progress.pending === 1 ? '' : 's'}`);
            if (progress.attention) pieces.push(`${progress.attention} requiere${progress.attention === 1 ? '' : 'n'} atención`);
            if (progress.skipped) pieces.push(`${progress.skipped} omitido${progress.skipped === 1 ? '' : 's'}`);
            parts.push(`<div>Descargas: ${pieces.join(' · ')}</div>`);
        }
        if (progress.items.length && progress.total > 1) {
            parts.push('<ul class="acquisition-progress-issues">' + progress.items.map(i =>
                `<li>${escapeHtml(i.label ?? i.Label ?? '')}: ${escapeHtml(i.reason ?? i.Reason ?? 'requiere revisión')}</li>`).join('') + '</ul>');
        }
        if (progress.skippedItems.length && progress.total > 1) {
            parts.push('<ul class="acquisition-progress-issues acquisition-progress-skipped">' + progress.skippedItems.map(i =>
                `<li>${escapeHtml(i.label ?? i.Label ?? '')}: ${escapeHtml(i.reason ?? i.Reason ?? 'omitido')}</li>`).join('') + '</ul>');
        }
        const nextAttempt = formatNextAttempt(nextAttemptUtc);
        if (nextAttempt) {
            parts.push(`<div>Próximo intento: ${escapeHtml(nextAttempt)}.</div>`);
        }
        if (progress.waitingForAir) {
            parts.push(`<div>Esperando emisión: ${escapeHtml(progress.waitingForAir)}.</div>`);
        }
        if (progress.following) {
            parts.push('<div>Siguiendo la emisión: los episodios nuevos se descargarán según se emitan. Cancela la solicitud para dejar de seguirla.</div>');
        }
        if (progress.awaitingSelection) {
            parts.push(`<div class="acquisition-progress-hint">Releases listas${progress.current ? ` para ${escapeHtml(progress.current)}` : ''}: pulsa «Seleccionar release» para elegir una.</div>`);
        }

        container.innerHTML = parts.join('');
        container.style.display = parts.length ? 'block' : 'none';
        return progress;
    }

    let lastDetailItemId = null;
    let lastDetailItem = null;

    function checkSeriesDetailsPage() {
        const mainDetail = document.querySelector('.page:not(.hide) .detailPagePrimaryContainer') ||
                           document.querySelector('.itemDetailPage[data-itemtype="Series"] .detailPagePrimaryContainer') ||
                           document.querySelector('.detailPagePrimaryContainer');
        if (!mainDetail || document.getElementById('btn-request-missing-episodes')) return;

        const buttonsBar = mainDetail.querySelector('.mainDetailButtons');
        if (!buttonsBar || typeof window === 'undefined' || !window.location || !window.ApiClient) return;

        const idMatch = (window.location.hash || '').match(/id=([a-fA-F0-9]{32})/);
        if (!idMatch) return;
        const itemId = idMatch[1];

        if (itemId === lastDetailItemId) {
            // Same page re-rendered: re-add the button only for a series already identified
            if (lastDetailItem) injectRequestMissingButton(buttonsBar, lastDetailItem);
            return;
        }

        // Only series pages get the button (R11); the item type is checked once per page
        lastDetailItemId = itemId;
        lastDetailItem = null;
        window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), itemId).then(item => {
            if (itemId !== lastDetailItemId || !item || item.Type !== 'Series') return;
            lastDetailItem = item;
            if (!document.getElementById('btn-request-missing-episodes')) {
                injectRequestMissingButton(buttonsBar, item);
            }
        }).catch(err => console.warn('[Acquisition] Error fetching item details:', err));
    }

    function injectRequestMissingButton(buttonsBar, item) {
        // A labelled button under the title, not another round icon in the buttons bar: there it looked like Jellyfin's
        // own "add to playlist" and on phones the bar overflows the screen and hid it
        const html = '<div class="acquisition-detail-request ' + themeClassName('acquisition-detail-row') + '">' +
            '<button type="button" id="btn-request-missing-episodes" class="acquisition-detail-btn" title="Pedir a la biblioteca los episodios o temporadas que faltan">' +
            '<span class="material-icons download" aria-hidden="true"></span><span>Pedir episodios que faltan</span>' +
            '</button></div>';
        // Right above the overview, which keeps the text column on desktop and on phones (where the title block is collapsed)
        const primary = buttonsBar.closest('.detailPagePrimaryContainer');
        const overview = primary && primary.querySelector('.detailSection .overview');
        const overviewSection = overview && overview.closest('.detailSection');
        if (overviewSection) {
            overviewSection.insertAdjacentHTML('afterbegin', html);
        } else {
            buttonsBar.insertAdjacentHTML('afterend', html);
        }
        const btn = document.getElementById('btn-request-missing-episodes');

        btn.addEventListener('click', async () => {
            const ids = item.ProviderIds || {};
            btn.disabled = true;
            try {
                await openLocalSeriesRequest({
                    title: item.Name || '',
                    year: item.ProductionYear,
                    tvdb: ids.Tvdb || ids.tvdb,
                    tmdb: ids.Tmdb || ids.tmdb,
                    imdb: ids.Imdb || ids.imdb
                });
            } finally {
                btn.disabled = false;
            }
        });

    }

    // Maps a series from the Jellyfin library to its Sonarr entry (by TVDB, TMDB or IMDb) and opens the request modal
    async function openLocalSeriesRequest(local) {
        const params = new URLSearchParams();
        params.set('title', local.title || '');
        if (local.year) params.set('year', String(local.year));
        if (local.tvdb) params.set('tvdb', local.tvdb);
        if (local.tmdb) params.set('tmdb', local.tmdb);
        if (local.imdb) params.set('imdb', local.imdb);

        try {
            // The server maps the local series to Sonarr's entry by TVDB, TMDB or IMDb (TVDB is often missing in Jellyfin)
            const resp = await fetch(`${getApiBase()}/Acquisition/catalog/local-series?${params.toString()}`, { headers: getAuthHeaders() });
            if (resp.ok) {
                openDetailsModal(await resp.json());
            } else {
                window.alert(await readErrorMessage(resp, 'No se pudo encontrar esta serie en Sonarr.'));
            }
        } catch (err) {
            console.warn('[Acquisition] Error resolving local series:', err);
            window.alert('Error de conexión al buscar la serie en Sonarr.');
        }
    }

    function escapeHtml(value) {
        if (value === null || value === undefined) return '';
        const str = String(value);
        return str.replace(/[&<>"']/g, (m) => {
            switch (m) {
                case '&': return '&amp;';
                case '<': return '&lt;';
                case '>': return '&gt;';
                case '"': return '&quot;';
                case "'": return '&#39;';
                default: return m;
            }
        });
    }

    if (typeof document !== 'undefined') {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', init);
        } else {
            init();
        }
    }

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = {
            normalizeCatalogItem,
            normalizeCatalogResponse,
            renderExternalResults,
            performExternalSearch,
            getAuthHeaders,
            createItemCard,
            themeClassName
        };
    }
})();
