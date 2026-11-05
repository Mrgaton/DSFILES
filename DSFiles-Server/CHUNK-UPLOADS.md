# Subidas por fragmentos entre réplicas

El protocolo 2 no guarda sesiones ni listas de fragmentos en el servidor. Cada
petición puede llegar a una réplica distinta, con las mismas `SIGNKEY` y `ENCRYPTKEY`.
No requiere afinidad, una base de datos ni archivos de estado locales.

## Configuración

Todas las réplicas necesitan SIGNKEY de al menos 32 bytes UTF-8 y ENCRYPTKEY de
exactamente 32 bytes UTF-8, aleatorias y secretas. Deben ejecutar la misma versión
del protocolo y mantener sus relojes sincronizados. Cambiar las claves invalida
autorizaciones y recibos existentes. La web y el servidor deben actualizarse juntos.

## Peticiones

1. `POST /upload-challenge`: cabeceras `filename`, `filesize`, `webhook` opcional.
   Devuelve el reto de trabajo habitual, autenticado y válido durante cinco minutos.
   Cualquier réplica puede verificar la solución; no existe un registro de retos.
2. `POST /cuh`: mismas cabeceras, `encrypt`, `Upload-Proof` y `Upload-Protocol: 2`.
   Devuelve `Session-ID` (JWE), `ChunkSize` y `Upload-Protocol: 2`. El token contiene
   nombre, tamaño, clave del archivo, credencial de Discord y un identificador
   aleatorio de subida, protegidos por firma y cifrado. Caduca a las 24 horas.
3. `POST /cuc`: cabeceras `Session-ID` y `chunk` (desde 1); cuerpo binario de tamaño
   exacto. Devuelve JSON `{ "receipt": "<JWT firmado>" }`. El recibo contiene el
   identificador de subida, índice, tamaño e IDs de Discord; no contiene credenciales.
   Los fragmentos son independientes y pueden llegar fuera de orden.
4. `POST /cuf`: cabecera `Session-ID` y JSON `{ "receipts": ["..."] }`. Debe incluir
   exactamente un recibo por fragmento. Se verifican firma, propósito, caducidad,
   pertenencia, índice, tamaño y ausencia de duplicados. Se ordenan los fragmentos
   y se devuelve el recibo final habitual: `name`, `seed`, `removeToken`, `webLink`.
   Esta operación no publica mensajes en Discord y puede repetirse en otra réplica.
5. `POST /cua`: mismo formato, con los recibos confirmados que se quieren eliminar.
   No admite recibos de otras subidas. Considera correctamente borrados los mensajes
   que Discord ya no encuentra. El token y los recibos deben seguir vigentes.

Los manifiestos admiten hasta 8 MiB; cada archivo, hasta 10.000 fragmentos. El token
de sesión tiene tamaño constante y se envía por cabecera; la lista creciente de
recibos viaja en el cuerpo final. Los semáforos solo limitan peticiones activas en
cada proceso: no mantienen estado por subida ni intervienen en su autorización.

## Reanudación y límites sin estado

El navegador conserva el token y los recibos en la memoria de la pestaña. Tras una
interrupción puede enviar los fragmentos pendientes a cualquier réplica y finalizar
con todos los recibos. Retirar una subida de la cola intenta limpiar los fragmentos
confirmados. La limpieza puede fallar por desconexión o caducidad.

No es posible garantizar un único uso global sin estado compartido. Un reto puede
reutilizarse para los mismos metadatos durante sus cinco minutos de validez. Subir
de nuevo el mismo índice puede crear otro mensaje en Discord; solo uno de sus
recibos puede formar parte del archivo final. Por eso la web no reintenta
automáticamente un fragmento cuando se desconoce si Discord lo aceptó. La
finalización y el borrado sí admiten repetición.

Si se pierde una respuesta con un recibo, ese fragmento puede quedar huérfano. Si
se cierra la pestaña sin enviar sus recibos, no hay un inventario del servidor con
el que limpiar la subida. Una limpieza garantizada, la revocación individual o el
uso único requerirían estado compartido o un servicio externo que lo proporcione.
Las cuotas globales y por IP deben aplicarse en el proxy si son necesarias.

La cola de caducidades de enlaces cortos es otra función y mantiene su persistencia;
no interviene en estas subidas. La ruta de compresión `/d` es una petición única.

## Verificación

`DSFiles-Tests/ReplicaUpload` ejecuta cada paso en un proceso nuevo contra un proveedor
Discord simulado. Comprueba transferencia cifrada de varios fragmentos, cambio de
réplica, orden arbitrario, reintento de finalización, abortos y rechazo de recibos
falsificados o pertenecientes a otro archivo. `chunk-upload.test.mjs` comprueba el
flujo real de la interfaz, incluida la conservación de recibos al reanudar.
