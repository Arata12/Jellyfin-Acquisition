// Jellyfin Acquisition Plugin Client Extension for Jellyfin Web 12.2
(() => {
    let currentAbortController = null;
    let debounceTimer = null;
    let lastQuery = '';
    let activeSearchInput = null;
    // The section and the query it shows; React may re-render the search page and drop or reorder foreign nodes
    let currentSection = null;
    let renderedQuery = '';

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
            statusText: item.statusText ?? item.StatusText ?? null
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

    function init() {
        document.addEventListener('input', (e) => {
            const target = e.target;
            if (target && (target.type === 'search' || target.classList.contains('searchFields') || target.id?.toLowerCase().includes('search'))) {
                activeSearchInput = target;
                onSearchInput(target.value);
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

    function onSearchInput(val) {
        clearTimeout(debounceTimer);
        const query = (val || '').trim();

        if (query.length < 3) {
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
                    ${isNotConfigured ? '<p><a href="#!/configurationpage?name=Acquisition" style="color: #00a4dc; text-decoration: underline;">Configurar catálogo externo en el panel de administración</a></p>' : ''}
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

    function createItemCard(rawItem) {
        const item = normalizeCatalogItem(rawItem);
        const card = document.createElement('div');
        card.className = 'acquisition-card';

        const apiBase = getApiBase();
        const posterSrc = item.posterUrl ? (item.posterUrl.startsWith('/') ? `${apiBase}${item.posterUrl}` : item.posterUrl) : 'data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" width="140" height="210" fill="%23333"><rect width="100%" height="100%"/></svg>';
        const kindLabel = item.kind === 'Movie' ? 'Película' : 'Serie';
        const yearLabel = item.year ? `(${item.year})` : '';

        let badgeHtml = '';
        if (item.requestStatus !== null && item.requestStatus !== undefined) {
            const label = STATUS_LABELS[item.requestStatus] || item.statusText || 'Solicitado';
            badgeHtml = `<span class="acquisition-badge status-${escapeHtml(String(item.requestStatus))}">${escapeHtml(label)}</span>`;
        }

        card.innerHTML = `
            <div class="acquisition-card-image-box">
                <img class="acquisition-card-image" src="${escapeHtml(posterSrc)}" alt="${escapeHtml(item.title)}" loading="lazy" />
                ${badgeHtml}
            </div>
            <div class="acquisition-card-content">
                <div class="acquisition-card-title">${escapeHtml(item.title)}</div>
                <div class="acquisition-card-meta">${kindLabel} ${yearLabel}</div>
            </div>
        `;

        card.addEventListener('click', () => {
            openDetailsModal(item);
        });

        return card;
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
        modal.remove();
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

    async function openDetailsModal(rawItem) {
        const item = normalizeCatalogItem(rawItem);
        const modal = document.createElement('div');
        modal.className = themeClassName('acquisition-modal-overlay');
        modal.id = 'acquisition-modal';

        const apiBase = getApiBase();
        const kindLabel = item.kind === 'Movie' ? 'Película' : 'Serie';
        const yearLabel = item.year ? `(${item.year})` : '';
        const posterSrc = item.posterUrl ? (item.posterUrl.startsWith('/') ? `${apiBase}${item.posterUrl}` : item.posterUrl) : '';

        modal.innerHTML = `
            <div class="acquisition-modal-content">
                <button class="acquisition-modal-close" id="modal-close">&times;</button>
                <div class="acquisition-modal-body">
                    <div class="acquisition-modal-sidebar">
                        ${posterSrc ? `<img src="${escapeHtml(posterSrc)}" class="acquisition-modal-poster" alt="${escapeHtml(item.title)}" />` : ''}
                    </div>
                    <div class="acquisition-modal-main">
                        <h2 class="acquisition-modal-title">${escapeHtml(item.title)} <span class="year">${yearLabel}</span></h2>
                        <div class="acquisition-modal-meta">${kindLabel}</div>
                        <p class="acquisition-modal-overview">${escapeHtml(item.overview || 'Sin descripción disponible.')}</p>

                        <div id="acquisition-destination-area" class="acquisition-destination-area" style="display: none; margin-bottom: 15px;">
                            <label for="acquisition-destination-select" style="display: block; font-weight: bold; margin-bottom: 5px;">Destino:</label>
                            <select id="acquisition-destination-select" class="emby-select" style="width: 100%; padding: 6px 10px; border-radius: 4px;"></select>
                        </div>
                        <p id="acquisition-request-error" style="display: none; color: #ff9800;"></p>

                        <div id="acquisition-action-area" class="acquisition-action-area">
                            <button id="btn-add-library" class="acquisition-btn-primary">Añadir a biblioteca</button>
                        </div>

                        <div id="acquisition-scope-area" class="acquisition-scope-area" style="display: none;">
                            <h3>Seleccionar contenido a solicitar</h3>
                            <div class="scope-options">
                                <label><input type="radio" name="scopeType" value="all" checked /> Serie completa</label>
                                <label><input type="radio" name="scopeType" value="custom" /> Seleccionar episodios</label>
                            </div>
                            <div id="episodes-container" class="episodes-checklist" style="display: none;">
                                <em>Cargando episodios...</em>
                            </div>
                            <div class="scope-actions">
                                <button id="btn-confirm-scope" class="acquisition-btn-primary">Confirmar y solicitar</button>
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
                                <button id="btn-cancel-request" class="acquisition-btn-secondary" style="display: none;">Cancelar solicitud</button>
                            </div>
                            <div id="releases-container" class="releases-selection-list" style="display: none; margin-top: 15px;"></div>
                        </div>
                    </div>
                </div>
            </div>
        `;

        document.body.appendChild(modal);

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
                    // Without a single option or an explicit default the user must choose; nothing is picked silently
                    const placeholder = defaultId ? '' : '<option value="" selected disabled>Elige un destino…</option>';
                    destSelect.innerHTML = placeholder + destinations.map(d => {
                        const id = d.id ?? d.Id ?? '';
                        const name = d.name ?? d.Name ?? '';
                        const seriesType = d.seriesType ?? d.SeriesType ?? '';
                        const rootFolderPath = d.rootFolderPath ?? d.RootFolderPath ?? '';
                        const typeSuffix = seriesType ? ` (${escapeHtml(seriesType)})` : '';
                        const pathSuffix = rootFolderPath ? ` - ${escapeHtml(rootFolderPath)}` : '';
                        const selected = defaultId && id === defaultId ? ' selected' : '';
                        return `<option value="${escapeHtml(id)}"${selected}>${escapeHtml(name)}${typeSuffix}${pathSuffix}</option>`;
                    }).join('');
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

    async function setupSeriesScopePicker(modal, item, getSelectedDestination, showError, existingRequestId) {
        const episodesContainer = modal.querySelector('#episodes-container');
        const radios = modal.querySelectorAll('input[name="scopeType"]');
        let createdRequest = existingRequestId ? { id: existingRequestId } : null;
        const getDestInfo = typeof getSelectedDestination === 'function' ? getSelectedDestination : () => ({ id: null });

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
            episodesContainer.style.display = 'block';
            await loadEpisodes();
        }

        radios.forEach(r => {
            r.addEventListener('change', async (e) => {
                if (e.target.value === 'custom') {
                    episodesContainer.style.display = 'block';
                    if (!createdRequest) {
                        const created = await createAcquisitionRequest(item, { all: false }, getDestInfo().id);
                        if (created.id) {
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

            const scopeType = modal.querySelector('input[name="scopeType"]:checked').value;
            if (scopeType === 'all') {
                if (!createdRequest) {
                    const created = await createAcquisitionRequest(item, { all: true }, getDestInfo().id);
                    if (!created.id) {
                        fail(created.error);
                        return;
                    }
                    createdRequest = created;
                } else if (!await confirmScope(createdRequest.id, { all: true, seasonNumbers: [], episodeNumbers: [], episodeIds: [] })) {
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
                hasFile: Boolean(e.hasFile ?? e.HasFile)
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

            let html = '';
            for (const sNum of Object.keys(seasons).sort((a,b) => a - b)) {
                html += `
                    <div class="season-block">
                        <label class="season-title">
                            <input type="checkbox" class="chk-season" value="${sNum}" />
                            <strong>Temporada ${sNum}</strong>
                        </label>
                        <div class="episodes-sublist">
                `;
                seasons[sNum].forEach(ep => {
                    html += `
                        <label class="episode-item ${ep.hasFile ? 'has-file' : ''}">
                            <input type="checkbox" class="chk-episode" value="${ep.episodeNumber}" data-id="${ep.id}" data-season="${sNum}" ${ep.hasFile ? 'disabled checked' : ''} />
                            <span>Ep. ${ep.episodeNumber}: ${escapeHtml(ep.title)} ${ep.hasFile ? '(Ya en biblioteca)' : ''}</span>
                        </label>
                    `;
                });
                html += `</div></div>`;
            }

            container.innerHTML = html;

            container.querySelectorAll('.chk-season').forEach(sBox => {
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
                destinationId: destinationId || null
            };

            const resp = await fetch(`${apiBase}/Acquisition/requests`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify(payload)
            });

            if (resp.status === 409) {
                const data = await resp.json();
                return { id: data.requestId || data.RequestId };
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
        const requestUrl = `${getApiBase()}/Acquisition/requests/${encodeURIComponent(requestId)}`;
        let stopped = false;

        btnRetry.addEventListener('click', async () => {
            btnRetry.disabled = true;
            await fetch(`${requestUrl}/retry`, { method: 'POST', headers: getAuthHeaders() });
            poll();
        });

        btnCancel.addEventListener('click', async () => {
            if (!window.confirm('¿Cancelar esta solicitud? Si ya se envió a qBittorrent, la descarga no se elimina.')) return;
            btnCancel.disabled = true;
            const resp = await fetch(requestUrl, { method: 'DELETE', headers: getAuthHeaders() });
            if (resp.ok) {
                stopped = true;
                statusText.textContent = 'Cancelada';
                statusDetail.textContent = 'La solicitud se ha cancelado.';
                [btnRetry, btnViewReleases, btnViewLocal, btnCancel].forEach(b => { b.style.display = 'none'; });
                releasesContainer.style.display = 'none';
            } else {
                btnCancel.disabled = false;
                statusDetail.textContent = await readErrorMessage(resp, 'No se pudo cancelar la solicitud.');
            }
        });

        btnViewReleases.addEventListener('click', async () => {
            if (releasesContainer.style.display === 'block') {
                releasesContainer.style.display = 'none';
                btnViewReleases.textContent = 'Seleccionar release';
                return;
            }

            releasesContainer.style.display = 'block';
            releasesContainer.innerHTML = '<em>Cargando releases candidatas...</em>';
            btnViewReleases.textContent = 'Ocultar releases';

            try {
                const resp = await fetch(`${requestUrl}/releases`, { headers: getAuthHeaders() });
                if (!resp.ok) {
                    releasesContainer.innerHTML = `<em>${escapeHtml(await readErrorMessage(resp, 'Error al cargar releases.'))}</em>`;
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
                    releasesContainer.innerHTML = '<em>No se encontraron releases disponibles.</em>';
                    return;
                }

                let html = '<div class="releases-table">';
                releases.forEach(rel => {
                    html += `
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
                });
                html += '</div>';
                releasesContainer.innerHTML = html;

                releasesContainer.querySelectorAll('.btn-select-release').forEach(btn => {
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
                releasesContainer.innerHTML = '<em>Error de conexión al cargar releases.</em>';
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
                    const req = {
                        status: statusName(reqRaw.status !== undefined ? reqRaw.status : reqRaw.Status),
                        attentionReason: reqRaw.attentionReason ?? reqRaw.AttentionReason,
                        errorMessage: reqRaw.errorMessage ?? reqRaw.ErrorMessage,
                        localItemId: reqRaw.localItemId ?? reqRaw.LocalItemId,
                        progress: reqRaw.progress || reqRaw.Progress || null,
                        history: (reqRaw.history || reqRaw.History || []).map(h => ({
                            message: h.message ?? h.Message,
                            status: statusName(h.status !== undefined ? h.status : h.Status)
                        }))
                    };
                    statusText.textContent = STATUS_LABELS[req.status] || req.status || 'Procesando';
                    statusDetail.textContent = req.attentionReason || req.errorMessage || (req.history?.length ? req.history[req.history.length - 1].message : '');
                    const progress = renderProgress(statusProgress, req.progress);

                    if (req.status === 'Available' && req.localItemId) {
                        btnViewLocal.style.display = 'inline-block';
                        btnViewLocal.onclick = () => {
                            window.location.hash = `#!/details?id=${encodeURIComponent(req.localItemId)}`;
                            modal.remove();
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

    // Summary of a request's downloads: counts, the one in progress, what needs attention and a manual-selection hint
    function renderProgress(container, raw) {
        const p = raw || {};
        const num = (a, b) => Number(p[a] ?? p[b] ?? 0) || 0;
        const progress = {
            total: num('total', 'Total'),
            available: num('available', 'Available'),
            inProgress: num('inProgress', 'InProgress'),
            pending: num('pending', 'Pending'),
            attention: num('attention', 'Attention'),
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
            parts.push(`<div>Descargas: ${pieces.join(' · ')}</div>`);
        }
        if (progress.items.length && progress.total > 1) {
            parts.push('<ul class="acquisition-progress-issues">' + progress.items.map(i =>
                `<li>${escapeHtml(i.label ?? i.Label ?? '')}: ${escapeHtml(i.reason ?? i.Reason ?? 'requiere revisión')}</li>`).join('') + '</ul>');
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
        // Same markup as Jellyfin's own detail buttons (round, icon only, tooltip), so it takes the active theme's look.
        // Inserted as HTML so Jellyfin upgrades it to an emby-button like the native ones.
        const html = '<button is="emby-button" type="button" id="btn-request-missing-episodes" class="button-flat btnAcquisitionRequest detailButton"' +
            ' title="Solicitar episodios faltantes" aria-label="Solicitar episodios faltantes">' +
            '<div class="detailButton-content"><span class="material-icons detailButton-icon playlist_add" aria-hidden="true"></span></div>' +
            '</button>';
        const moreButton = buttonsBar.querySelector('.btnMoreCommands');
        if (moreButton) {
            moreButton.insertAdjacentHTML('beforebegin', html);
        } else {
            buttonsBar.insertAdjacentHTML('beforeend', html);
        }
        const btn = document.getElementById('btn-request-missing-episodes');

        btn.addEventListener('click', async () => {
            const ids = item.ProviderIds || {};
            const params = new URLSearchParams();
            params.set('title', item.Name || '');
            if (item.ProductionYear) params.set('year', String(item.ProductionYear));
            if (ids.Tvdb || ids.tvdb) params.set('tvdb', ids.Tvdb || ids.tvdb);
            if (ids.Tmdb || ids.tmdb) params.set('tmdb', ids.Tmdb || ids.tmdb);
            if (ids.Imdb || ids.imdb) params.set('imdb', ids.Imdb || ids.imdb);

            btn.disabled = true;
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
            } finally {
                btn.disabled = false;
            }
        });

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
