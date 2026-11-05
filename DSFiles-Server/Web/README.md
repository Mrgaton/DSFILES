# DS Files Web

Interfaz estática servida en `/` y `/web/`, sin compilador ni dependencias de navegador.
El servidor copia los recursos al compilar y publicar. En Debug escucha normalmente en
`http://localhost:8081/`. Las secciones usan `#upload`, `#files`, `#links` y `#animation`.

Para trabajar con la interfaz y reenviar las peticiones a una API local:

```powershell
node DSFiles-Server/Web/preview.mjs http://127.0.0.1:8081
```

La vista previa escucha en `http://127.0.0.1:4173` y no se incluye en la publicación.

## Archivos y enlaces

- Las subidas usan un reto de acceso y `/cuh` + `/cuc` + `/cuf`. Con compresión automática
  usan `/d`, con un máximo de 100.000.000 bytes. El navegador respeta `ChunkSize`
  y `Session-ID`. El reto se resuelve localmente en un worker. El protocolo 2 usa
  un token cifrado y recibos firmados por fragmento: cada paso puede atenderlo una
  réplica diferente con las mismas claves. Véase `../CHUNK-UPLOADS.md`.
- El cifrado de archivos está activado por defecto. La clave forma parte del enlace;
  cualquier persona que conserve el enlace puede descargar el archivo.
- El webhook predeterminado pertenece al servidor. Se puede guardar uno personal
  en Settings. Los nombres se normalizan a ASCII y conservan su extensión dentro
  del límite de 64 caracteres. Los archivos vacíos se omiten.
- El ZIP se crea localmente con ZIP32 y método STORE: agrupa sin recomprimir.
  Conserva los nombres originales y renombra duplicados. Límite: 4 GB y 65.535 entradas.
- Cada archivo conserva su progreso, estado, enlace y acciones en la misma fila.
  El progreso distingue el envío del fragmento de su confirmación; el 100 % se
  muestra al recibir el enlace o recibo final. Los archivos completados no se
  vuelven a enviar al reintentar los pendientes. Un ZIP ocupa una fila con su lista
  de archivos originales.
- Los reintentos seguros muestran una cuenta atrás, respetan `Retry-After` y se
  limitan a tres por petición. Los fragmentos no se repiten automáticamente tras
  un fallo ambiguo; la finalización sí puede repetirse usando los mismos recibos.
- El tiempo restante aprende de hasta 60 observaciones de transferencias confirmadas,
  separadas por compresión, cifrado y destino. Ajusta coste fijo y tiempo por byte
  con más peso para datos recientes y menos para valores atípicos. El intervalo es
  orientativo y se recalcula durante la subida; al principio muestra que está
  aprendiendo. Solo guarda tipo, bytes y tiempos como preferencia del catálogo,
  bajo su misma protección y sincronización. No guarda nombres ni enlaces en ese historial.
- Cancelar interrumpe la petición actual y conserva los recibos confirmados en la
  pestaña para reanudar. Al retirar un archivo de la cola, la web intenta borrar sus
  fragmentos mediante `/cua`. Cerrar la pestaña pierde esos recibos; el servidor no
  puede limpiar automáticamente una subida abandonada sin guardar estado.
  No se reintentan automáticamente subidas que podrían haberse procesado.
- Files contiene el historial de esta web, no un listado de archivos personales de
  Drive. Muestra 50 registros por página y permite buscar, ordenar, descargar y borrar.
  Los archivos antiguos sin un registro guardado no se pueden descubrir automáticamente.
- Los errores de operaciones conservan los tokens de recuperación y los recuentos
  de borrados parciales. Los recibos JWT requieren el intermediario para recuperar el enlace.

Links envía la URL original sin cifrar a JSPaste y genera `/s/{clave}`. El cifrado de
archivos no se aplica al acortador. JSPaste puede eliminar sus datos; sus documentos
anónimos pueden borrarse directamente sin el token de DS Files. Hay que conservar
la URL original. Los enlaces antiguos de Discord siguen siendo compatibles.

La caducidad elimina únicamente el enlace corto. DS Files guarda la tarea en
`Data/shortlink-expiry.json`, junto al ejecutable, y revisa los vencimientos cada
15 segundos. Si el servidor o JSPaste no están disponibles, reintenta después.
La programación admite entre un minuto y un año, o ninguna caducidad solicitada.
Una programación fallida devuelve los datos de recuperación y no se declara exitosa.

La carpeta `Data` debe ser escribible, persistente y quedar fuera de Web. Hay que
conservarla al migrar el servidor. La cola admite 10.000 pendientes y requiere un
único proceso propietario. Una cola ilegible detiene el arranque para no sobrescribirla.

## Catálogo, privacidad y Google Drive

El catálogo funciona localmente sin una contraseña adicional. Contiene enlaces,
tokens de borrado y preferencias, incluido el webhook. El cifrado opcional se activa
en Account and backup → Catalog privacy, con una contraseña de al menos 12 caracteres.
Es independiente del cifrado de los archivos.

El catálogo cifrado usa AES-256-GCM y PBKDF2-SHA256 con 600.000 iteraciones. Protege
historial y preferencias; la autorización de Google recordada utiliza un bloque
cifrado separado y exclusivo de este navegador. Esa autorización se excluye de
respaldos y sincronización. Sin cifrado opcional, los datos locales y los respaldos
se guardan sin esa protección adicional.

Al abrir el catálogo se incorporan los ajustes antiguos que falten y se eliminan
sus copias en texto claro después de guardar correctamente. Los valores existentes
en el catálogo prevalecen. Los catálogos cifrados antiguos necesitan desbloquearse
para migrar sus ajustes y autorización. Un fallo de escritura conserva el estado
anterior de protección. Los respaldos exportados anteriormente no se modifican.

Drive combina el catálogo y las preferencias entre dispositivos; los archivos siguen
en DS Files. Las copias remotas se convierten al sincronizar. Si falla la limpieza,
pueden permanecer copias anteriores y hay que reintentar Sync. Los cambios locales
permanecen pendientes sin conexión. La sesión tiene caducidad y requiere reconectar;
no utiliza refresh tokens. Bloquear o desconectar elimina la autorización recordada.

Exportar permite conservar un respaldo e importar combina registros sin resucitar
borrados confirmados. Límite: 4 MiB y 20.000 registros, sujeto a la cuota del navegador.
No hay recuperación de contraseñas. Conviene exportar antes de borrar los datos del sitio.

### Configuración de Google

1. Habilitar Google Drive API en Google Cloud y configurar la audiencia OAuth.
2. Autorizar únicamente `https://www.googleapis.com/auth/drive.appdata`.
3. Crear un cliente OAuth de tipo Web application y registrar el origen exacto,
   por ejemplo `https://files.example.com` o `http://localhost:8081`, sin rutas.
4. Guardar su Client ID público en `config.js`. No añadir un Client Secret ni una API key.
5. Abrir Account and backup y conectar Google Drive.

El flujo usa un popup y no necesita una ruta de callback. Una CSP propia debe permitir
GIS de `accounts.google.com` y conexiones a Google APIs. Al cambiar de origen hay que
autorizar el nuevo origen. Un cliente OAuth distinto puede no acceder al catálogo
anterior; en ese caso se necesita el respaldo. Los enlaces se reconstruyen con el
servidor actual, que debe ser compatible con las semillas y sus archivos.

## Animación

Animation genera un comando curl para PowerShell o Bash/Zsh. La vista previa muestra
la imagen original; la salida ANSI se ejecuta en una terminal. El servidor ajusta el
divisor vertical por la proporción de las celdas. Ctrl+C detiene la reproducción.

## Pruebas

```powershell
node --test DSFiles-Tests/web.test.mjs DSFiles-Tests/chunk-upload.test.mjs DSFiles-Tests/upload-timing.test.mjs
```

Las pruebas aíslan el almacenamiento y simulan las respuestas de la API. No contactan
con Google, Discord o JSPaste. Las comprobaciones de Native AOT están en
`DSFiles-Tests/Native`; la construcción Docker se describe en `DSFiles-Docker/NATIVE-AOT.md`.
