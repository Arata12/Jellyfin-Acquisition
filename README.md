# Jellyfin Acquisition

Plugin para **Jellyfin 12.2 – 12.4** que permite buscar y pedir series y películas que todavía no están en tu biblioteca, directamente desde el buscador de Jellyfin. El plugin se encarga del resto: registra el título en Sonarr o Radarr, busca la mejor release en Prowlarr, la envía a qBittorrent, espera a que Sonarr/Radarr la importe y comprueba que Jellyfin la detecta.

## Funciones

- **Buscador ampliado:** al buscar en Jellyfin aparece un bloque «Fuera de tu biblioteca» con los resultados de Sonarr y Radarr que aún no tienes, debajo de los resultados locales.
- **Solicitudes desde la ficha:** elige destino (carpeta raíz de Sonarr/Radarr), serie completa o episodios concretos, y sigue el estado de la descarga sin salir de Jellyfin.
- **Botón «Solicitar faltantes»** en la ficha de las series que ya tienes, para pedir los episodios que faltan.
- **Selección de release automática o manual:**
  - *Automática:* entre las releases válidas gana la que mejor encaja con tus preferencias (resolución, fuente y códec) y, a igual calidad, la de más semillas.
  - *Manual:* el plugin busca y te deja la lista; no se descarga nada hasta que eliges.
- **Series de varios episodios:** se procesan de uno en uno; si uno falla, queda apartado con su motivo y se continúa con el siguiente. «Reintentar» vuelve a poner en cola los apartados.
- **Series en emisión:** «Serie completa» sigue la serie y descarga los episodios nuevos según se emiten. Los episodios que pidas antes de su emisión esperan a que salgan.
- **Compatible con qBittorrent 4.x y 5.x.** Cada torrent se marca con una etiqueta `acq-…` para seguirlo, que se borra cuando la descarga está importada (opcional).
- **Estilo opcional** del buscador: clásico de Jellyfin o el del tema [Abyss](https://github.com/AumGupta/abyss-jellyfin), con detección automática.
- **API compatible con Seerr (opcional):** clientes con integración Seerr, como [plezy](https://github.com/edde746/plezy), pueden buscar y pedir a través del plugin sin instalar Seerr. Ver [Clientes Seerr (plezy)](#clientes-seerr-plezy).

## Requisitos

| Componente | Versión |
|---|---|
| Jellyfin | 12.2 – 12.4 |
| Sonarr | v4 (series) |
| Radarr | v5 o superior (películas) |
| Prowlarr | con al menos un indexador de torrents |
| qBittorrent | 4.x o 5.x, con la interfaz web activada |

Sonarr y Radarr son independientes: puedes usar solo uno de los dos. Jellyfin debe poder conectar con todos los servicios por red.

## Instalación

1. Compila el plugin (ver [Compilar](#compilar)) y crea una carpeta `Acquisition` con estos archivos:
   ```
   Acquisition/
   ├── Jellyfin.Plugin.Acquisition.dll
   ├── Jellyfin.Plugin.Acquisition.deps.json
   ├── Jellyfin.Plugin.Acquisition.pdb
   ├── Jellyfin.Plugin.Acquisition.xml
   └── meta.json
   ```
2. **Detén Jellyfin.** Copiar la DLL con el servidor en marcha puede corromperla en memoria.
3. Copia la carpeta `Acquisition` dentro de la carpeta `plugins` de Jellyfin:
   - Docker: `/config/plugins/Acquisition/`
   - Paquete de Linux: `/var/lib/jellyfin/plugins/Acquisition/`

   Si tenías una versión anterior del plugin, borra antes su carpeta (puede llamarse `Acquisition` o `Acquisition_<versión>`).
4. Arranca Jellyfin. En **Panel de control → Complementos** debe aparecer *Acquisition* como activo.

## Configuración

En **Panel de control → Complementos → Acquisition**:

1. **Conexiones:** URL y API key de Sonarr, Radarr y Prowlarr, y URL, usuario y contraseña de qBittorrent.
2. Pulsa **«Cargar opciones desde servicios»**. El plugin lee de Sonarr y Radarr sus carpetas raíz, perfiles de calidad y clientes de descarga.
3. **Perfil técnico:** si Sonarr o Radarr tienen varios perfiles de calidad, elige cuál usar para dar de alta títulos nuevos. Solo se usa al registrar; la calidad de las releases la deciden las preferencias del plugin.
4. **Destinos:** añade las carpetas raíz que quieres ofrecer (por ejemplo *Anime* y *Series*) con su nombre visible y, en series, su tipo (`standard`, `anime` o `daily`). Si no añades ninguna, se ofrecen todas las carpetas de Sonarr/Radarr y el usuario elige.
5. **Categoría de qBittorrent:** déjala vacía para usar la del cliente qBittorrent configurado en Sonarr/Radarr. Rellénala solo si no se puede leer de ellos.
6. **Preferencias de releases:** resolución, fuente y códec preferidos, límites de tamaño y modo de selección (automática o manual).
7. **Permisos:** los administradores siempre pueden pedir contenido. Para el resto, activa «Permitir a todos los usuarios» o indica sus IDs.

### Apariencia

La opción **«Estilo de “Fuera de tu biblioteca”»** admite:

- **Automático:** usa el estilo de Abyss si ese tema está instalado y, si no, el de Jellyfin.
- **Jellyfin clásico.**
- **Abyss**, aunque el tema no esté instalado.

Los colores y medidas se pueden ajustar desde **Panel de control → General → CSS personalizado**:

```css
:root {
  --acquisition-accent: 255, 90, 90;   /* color principal (R, G, B) */
  --acquisition-radius: 16px;          /* redondeo */
  --acquisition-surface: 30, 30, 40;   /* fondo del modal (R, G, B) */
  --acquisition-blur: 6px;             /* desenfoque */
}
```

Si usas Abyss, carga una versión fija del tema (un commit concreto) en lugar de `@main`, para que el CSS no cambie sin que lo revises.

## Uso

- **Pedir algo nuevo:** busca en Jellyfin, abre una tarjeta de «Fuera de tu biblioteca», elige destino y pulsa «Añadir a biblioteca». En series puedes pedir la serie completa o elegir episodios.
- **Ver el estado:** vuelve a abrir la tarjeta (o la ficha de la serie) para ver el estado, el resumen de descargas y los motivos de lo que necesite atención.
- **Elegir release:** en modo manual, o cuando una descarga necesita atención, usa «Seleccionar release».
- **Cancelar:** «Cancelar solicitud» deja de seguir la solicitud; lo que ya se envió a qBittorrent no se borra.

El proceso continúa en el servidor aunque cierres el navegador.

### Clientes Seerr (plezy)

El plugin puede responder como un servidor Seerr, así que plezy (y otros clientes con integración Seerr) piden contenido al plugin directamente. No hace falta instalar Seerr.

1. En la configuración del plugin, activa **«API compatible con Seerr»**. Opcionalmente, añade una **clave de TMDB** para las filas de Explorar (populares, tendencias, próximos estrenos) y las recomendaciones. Sin ella, la búsqueda y las solicitudes funcionan igual.
2. En plezy: **Ajustes → Servicios → Seerr**, con la URL `https://tu-dominio[/jellyfin]/Acquisition/seerr`.
3. Inicia sesión con tu usuario y contraseña de Jellyfin, o con Quick Connect.

Qué cambia respecto a la interfaz web:

- **Temporadas:** se piden por temporadas o la serie completa (que sigue la emisión), en la numeración de Sonarr (TVDB). Los episodios sueltos y la elección manual de release solo están en la web.
- **Selección de release:** las solicitudes hechas desde plezy usan siempre la selección automática, aunque el plugin esté en modo manual.
- **Destino:** si hay varios destinos, se eligen en «Avanzado».
- **Episodios por temporada:** para las series que aún no están en Sonarr, el plugin los lee del servicio de metadatos de Sonarr (`skyhook.sonarr.tv`), así que Jellyfin necesita salida a internet.

Las solicitudes respetan los permisos del plugin y se pueden seguir y gestionar desde Jellyfin como cualquier otra.

## Solución de problemas

- **No aparece el bloque «Fuera de tu biblioteca»:** algunos bloqueadores de anuncios impiden que se cargue; añade tu dominio a sus excepciones. Tras actualizar el plugin, recarga la página con Ctrl+Shift+R una vez.
- **Una solicitud se queda en «Requiere atención»:** el modal muestra el motivo (perfil técnico sin elegir, categoría de qBittorrent, carpeta raíz eliminada, etc.). Corrígelo y pulsa «Reintentar».
- **Registros:** los mensajes del plugin aparecen en el log de Jellyfin con el prefijo `Jellyfin.Plugin.Acquisition`.

## Compilar

Necesitas el SDK de [.NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build src/Jellyfin.Plugin.Acquisition -c Release
```

Los archivos quedan en `src/Jellyfin.Plugin.Acquisition/bin/Release/net10.0/`. Copia los cuatro `Jellyfin.Plugin.Acquisition.*` a la carpeta `Acquisition` de la [instalación](#instalación); el `meta.json` es opcional, Jellyfin lo crea al cargar el plugin si no existe.

### Pruebas

```bash
dotnet test tests/Jellyfin.Plugin.Acquisition.Tests
```

Algunas pruebas del cliente web usan [Node.js](https://nodejs.org/), que debe estar instalado.

## Licencia

El código fuente de este proyecto se publica bajo la [licencia MIT](LICENSE).

El plugin compilado enlaza con las bibliotecas de Jellyfin (`Jellyfin.Controller` y `Jellyfin.Model`), que se distribuyen bajo GPL-3.0. Por eso, como indica la [guía de plugins de Jellyfin](https://github.com/jellyfin/jellyfin-plugin-template#licensing), **la DLL compilada queda sujeta a la GPL-3.0**.
