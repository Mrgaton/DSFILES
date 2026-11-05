# Kestrel para DS Files

`Helpers/FileServerKestrel.cs` configura Kestrel y los límites por ruta desde
`Program.cs`. El origen HTTP escucha sin TLS en 8080 (8081 en Debug); la configuración
Docker incluye Cloudflare Tunnel. El listener QUIC de UDP 20022 es un protocolo
propio y no es HTTP/3 de Kestrel.

## Configuración

| Opción | Valor |
| --- | --- |
| AllowSynchronousIO | false; se habilita únicamente en `/animate` |
| AddServerHeader | false |
| MaxRequestBodySize | 100.000.000 bytes |
| MaxRequestBufferSize | 1 MiB |
| MaxResponseBufferSize | 64 KiB |
| MaxRequestLineSize | 65.535 bytes para admitir semillas largas |
| MaxRequestHeadersTotalSize | 65.535 bytes |
| Protocolo HTTP de Kestrel | HTTP/1 en el puerto sin TLS |

Los cuerpos se limitan antes de que los handlers reserven memoria:

| POST | Máximo |
| --- | --- |
| `/cuc` | CHUNK_SIZE (20.971.265 bytes) |
| `/cuf`, `/cua` | 8 MiB; manifiesto de recibos firmados |
| `/d` | 100.000.000 bytes |
| `/shorten` | 8.192 bytes |
| `/cuh`, `/upload-challenge`, `/delete-challenge`, `/remove` | Sin cuerpo |

Las demás opciones conservan los valores del runtime. Los buffers mantienen
contrapresión para lectores lentos; quitarlos no garantiza mayor rendimiento.
La excepción de E/S síncrona existe porque el conversor ANSI escribe de esa forma.

## Comprobación de rendimiento

Medir por separado el origen con una API simulada, Discord y Cloudflare. Comparar
throughput, latencia p95, memoria/GC, cola de ThreadPool y errores con concurrencia
y clientes lentos. Las llamadas `.Result`, `Thread.Sleep`, `GC.Collect` y las reservas
por fragmento de la aplicación necesitan mediciones independientes de Kestrel.

Referencias: [opciones de Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/options)
y [endpoints](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints).
