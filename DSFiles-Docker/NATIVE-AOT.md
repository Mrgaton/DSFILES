# Imagen Docker con Native AOT

El `DOCKERFILE` principal publica el servidor como un ejecutable nativo Linux x64.
La imagen final usa `runtime-deps`, sin el runtime compartido de .NET ni el SDK.
El recolector de basura y los componentes de .NET necesarios quedan incorporados
en el ejecutable; siguen siendo necesarias las bibliotecas del sistema, SkiaSharp
y `libmsquic` para QUIC. El compilador Clang permanece en la etapa de construcción.

Desde la raíz del repositorio:

```powershell
docker build --platform linux/amd64 -f DOCKERFILE -t dsfiles-server:native-aot .
```

Las comprobaciones de QUIC, JSON, persistencia, JWT y Skia están en
`DSFiles-Tests/Native`, fuera del ejecutable de producción. Desde la raíz:

```powershell
dotnet run --project DSFiles-Tests/Native/DSFiles.NativeTests.csproj
```

Para comprobarlas también como binario nativo, publicar ese proyecto en Linux con
`-r linux-x64 -p:NativeAot=true` y ejecutarlo con las mismas bibliotecas de la imagen.
Las pruebas usan datos temporales y no contactan con Google, Discord o JSPaste.

El Docker Compose existente utiliza este Dockerfile. `Dockerize.bat` también lo
utiliza y conserva su publicación explícita en Docker Hub al ejecutar ese script.
Construir con `docker build` no publica la imagen ni reemplaza contenedores abiertos.

## Compatibilidad

- La configuración está verificada para `linux/amd64`. No publica un binario ARM64.
- El proyecto sigue usando la versión RC de .NET 11 que ya empleaba la imagen.
  `global.json` identifica ahora una versión válida del SDK.
- Las compilaciones normales de desarrollo siguen usando JIT. Native AOT se activa
  en Docker mediante `-p:NativeAot=true` y requiere compilar en Linux para Linux.
- JSON utiliza contratos generados en compilación; no se ha habilitado reflexión
  como alternativa que ocultaría fallos de compatibilidad al recortar metadatos.
- Se mantienen los puertos HTTP 8080 y QUIC UDP 20022, las variables de configuración
  y la carpeta `Data`. Hay que conservar esa carpeta al reemplazar el contenedor,
  igual que en la versión anterior.

Native AOT puede mejorar arranque y memoria. El tamaño final también depende del
RID y de las bibliotecas nativas incluidas. El rendimiento de transferencias debe
medirse con la carga real y los servicios externos.

Referencias: [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/),
[imágenes de .NET](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images).
