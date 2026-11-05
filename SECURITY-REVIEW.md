# Revisión de seguridad — 5 de noviembre de 2026

Se revisaron las rutas HTTP, las solicitudes salientes, las escrituras locales y
los relés WebSocket/QUIC. Las comprobaciones se ejecutaron localmente, con un
servidor señuelo y respuestas de Discord simuladas; no se atacaron servicios reales.

## Hallazgos y cambios

| Hallazgo | Evidencia | Corrección |
| --- | --- | --- |
| SSRF por webhook (alta) | La versión anterior de `/cuh` leyó del señuelo en localhost; el helper también pudo enviarle un archivo. | HTTPS, puerto 443, hosts exactos de Discord y ruta de webhook validada antes de la solicitud. |
| SSRF por `/animate` (alta) | La versión anterior descargó una imagen del señuelo en localhost. | Cliente sin proxy, cookies ni redirecciones; bloqueo de IP privadas/reservadas y conexión a las IP ya comprobadas para evitar una segunda resolución DNS. Se limita también la URL resultante de Tenor. |
| Credencial del webhook del servidor en recibos (alta) | `RemoveToken` incluía el ID y el token del webhook; la API podía devolverlos en claro. | Los nuevos recibos para ese webhook contienen una capacidad de borrado cifrada y autenticada, válida solo para los IDs incluidos. Los webhooks propios del usuario conservan el formato anterior. |
| Consumo de memoria y CPU | Semillas comprimidas sin límite de salida, tamaños de sesión arbitrarios e imágenes sin presupuesto de procesamiento. | Semillas hasta 256 KiB descomprimidos; nombre hasta 4 KiB; hasta 10.000 fragmentos por archivo; 8 peticiones activas del protocolo por fragmentos y 4 subidas tradicionales; límites de bytes, píxeles, fotogramas y concurrencia en animación. |
| Errores con detalles internos | Las descargas devolvían excepciones y rutas del código. | Mensajes públicos genéricos y detalles en el registro del servidor. |
| Peticiones simultáneas al mismo fragmento | El estado de una sesión podía modificarse a la vez y la limpieza podía caducar una subida activa. | El protocolo 2 no comparte estado mutable por subida: cada fragmento devuelve un recibo firmado y se comprueba el manifiesto al finalizar. Rechaza índices duplicados en el archivo final, aunque repetir una subida puede crear mensajes adicionales sin estado compartido. |
| Archivos de semillas acumulados | La subida tradicional podía crear un registro local por archivo. | El servidor deja de guardar esos registros; el cliente conserva esa opción. |
| Memoria del relé QUIC (alta) | El tamaño recibido se usaba directamente para reservar un array; la cola de salida era ilimitada. | Mensajes de hasta 1 MiB, cola limitada a 128 mensajes y 8 MiB por receptor, con desconexión de receptores saturados. También se rechazan IDs duplicados en una misma sala. Los clientes que envían mensajes mayores deben fragmentarlos. |

La precarga de 50 URLs de Discord y su caché en memoria se conservan. También se
mantiene el sandbox HTTP para los documentos subidos.

## Despliegue y credenciales existentes

- Recompilar y reiniciar. Las pruebas no reiniciaron la instancia existente.
- El borrado usa `srv:<JWE>:v2`, firmado y cifrado con `SIGNKEY` y `ENCRYPTKEY`.
  No requiere claves locales de Data Protection ni guardar un registro por archivo.
  Todas las réplicas deben tener las mismas claves: SIGNKEY de al menos 32 bytes
  UTF-8 y ENCRYPTKEY de exactamente 32 bytes UTF-8, generadas aleatoriamente.
  La versión local experimental `srv:...:v1` deja de emitirse y no se acepta.
- La autorización de borrado no caduca a la hora como el recibo JWT tradicional:
  debe seguir permitiendo eliminar un archivo antiguo. Cambiar las claves invalida
  los tokens existentes; revocar un token individual requeriría almacenar estado.
- Los retos de subida y borrado también se firman y verifican sin memoria compartida, con
  cinco minutos de validez y vinculados al token del archivo. Pueden reutilizarse
  dentro de ese plazo para el mismo borrado, que es idempotente; garantizar un único
  uso global exigiría estado compartido.
- Las subidas por fragmentos usan una autorización cifrada y recibos firmados;
  no hay sesiones ni retos guardados en memoria o disco. Véase `DSFiles-Server/CHUNK-UPLOADS.md`.
- La cola de caducidades de enlaces sigue usando `Data/shortlink-expiry.json` y
  necesita persistencia. Es una función independiente del protocolo de subida.
- Si el webhook del servidor ya se usó públicamente, considerar su credencial
  expuesta y regenerarla en Discord. Cifrar recibos nuevos no revoca los antiguos.
  La rotación puede invalidar operaciones con recibos anteriores; conservar un
  inventario privado de IDs para la administración de archivos existentes.
- Los documentos antiguos cacheados pueden conservar cabeceras sin sandbox.
  Purgar CDN/proxy y comprobar con una URL nueva o sin caché del navegador.

## Límites de la revisión

No se identificó una ruta pública que ejecute comandos, cargue ensamblados subidos
o escriba/lea una ruta local arbitraria indicada por el usuario. El generador de
certificados recibe un nombre fijo en el arranque; su antiguo handler de consulta
no está mapeado como ruta HTTP.

Los relés WebSocket y QUIC son anónimos: conocer una sala/pool permite participar.
No deben tratarse como canales privados autenticados. Los endpoints de prueba de
velocidad consumen ancho de banda por diseño. Para publicación abierta conviene
añadir cuotas por usuario/IP en el proxy, autenticación donde proceda y filtrado
de salida hacia redes internas como segunda barrera; los límites globales de esta
revisión no evitan toda denegación de servicio.

El JavaScript subido se ejecuta en el navegador, aislado del almacenamiento de
DSFiles. El sandbox no es un cortafuegos del equipo del visitante: aún puede intentar
peticiones de red sujetas a las políticas del navegador. Impedir toda comunicación
requeriría una CSP más restrictiva y limitaría las webs de prueba que se pueden subir.

No se encontraron avisos en la consulta de dependencias vulnerables de NuGet del
servidor, incluidas las transitivas. Esto no demuestra la ausencia de fallos en las
bibliotecas nativas ni sustituye una prueba del despliegue real.

## Pruebas repetibles

Proyecto: `DSFiles-Tests/Security/Security.csproj`. Usa un puerto local efímero y
respuestas simuladas, sin contactar con Discord ni con servicios privados reales.
Ejecutar con el SDK .NET 11 instalado mediante `dotnet run --project` y la ruta del
proyecto. Si la instancia de desarrollo mantiene DLL bloqueadas, usar una carpeta
de salida independiente con `-p:OutputPath=...`.

Referencia: [prevención de SSRF de OWASP](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).
