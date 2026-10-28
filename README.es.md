<p align="center">
  <img src="src/CHC.App/Assets/chc.png" width="96" alt="Logotipo de CHC">
</p>

<h1 align="center">CHC – Computer Health Check</h1>

<p align="center">
  Una aplicación gratuita para Windows que revisa la salud de tu PC (procesador, memoria, discos, tarjeta gráfica, batería, placa base y estabilidad del sistema) y explica los resultados, desde un resumen en lenguaje sencillo hasta los datos técnicos en bruto.
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <b>Español</b> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

## Características

- **Puntuación de salud (0–100)** para todo el equipo y para cada componente, con estados claros: *Correcto / Consejo / Advertencia / Crítico*.
- **Problemas y recomendaciones.** Cada resultado explica qué falla, por qué importa y qué puedes hacer.
- **De lo sencillo a lo técnico.** Cada página de componente empieza con un breve resumen y sigue con valores en vivo, especificaciones técnicas, tablas (módulos de memoria, atributos SMART, registro de eventos) y todos los sensores en bruto.
- **Procesador:** carga, temperatura, frecuencias, potencia, núcleos e hilos, caché, virtualización.
- **Memoria:** uso, memoria comprometida, módulos por ranura (tipo, velocidad, número de pieza), detección de canal único y de XMP/EXPO.
- **Almacenamiento:** estado de salud de Windows y SMART completo (el mismo motor que CrystalDiskInfo, a través de DiskInfoToolkit): vida restante, temperatura, horas de funcionamiento, total escrito, sectores reasignados, pendientes e incorregibles, advertencias críticas NVMe y espacio libre por unidad.
- **Gráficos:** temperatura, punto caliente, temperatura de la memoria, carga, potencia, VRAM, versión y antigüedad del controlador.
- **Batería** (portátiles): nivel de desgaste, capacidad de diseño y de carga completa, ciclos.
- **Placa base y sistema:** versión y antigüedad de la BIOS/UEFI, arranque seguro, TPM, versión de Windows y tiempo activo.
- **Estabilidad:** apagados inesperados, pantallazos azules, errores de hardware WHEA y errores de disco de los últimos 30 días, además de los problemas del Administrador de dispositivos.
- **Sensores en vivo** de todo lo que LibreHardwareMonitor puede leer, con filtro.
- **Informe PDF** con un clic y un diseño listo para usar. También exportación JSON y un resumen de texto para pegar en foros. Los números de serie se ocultan a menos que los actives.
- **8 idiomas:** English, Türkçe, Deutsch, Español, Français, Português (Brasil), Русский, 简体中文. Se pueden añadir más sin recompilar.
- Tema claro y oscuro (sigue a Windows), diseño Fluent de Windows 11.

## Descarga y ejecución

1. Descarga `CHC.exe` desde la página de [Releases](../../releases).
2. Ejecútalo. CHC necesita [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (si falta, Windows ofrece instalarlo la primera vez).

Sin permisos de administrador, CHC se inicia en **modo limitado**. Haz clic en **Ejecutar como administrador** en la barra lateral para ver los datos SMART y más sensores.

La temperatura de la CPU y los sensores de la placa base (ventiladores, voltajes) también necesitan el controlador gratuito y firmado [PawnIO](https://pawnio.eu), que usan las herramientas modernas de monitorización. CHC funciona sin él y te avisa cuando falta.

> CHC solo lee información. Nunca cambia ajustes, no hace overclock ni escribe en tus discos.

## Compilar desde el código fuente

Requisitos: Windows 10/11 x64 y el [SDK de .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/aliinal0/CHC.git
cd CHC
dotnet run --project src/CHC.App
```

Crear un único `CHC.exe`:

```bash
dotnet publish src/CHC.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

¿Quieres cambiar un umbral o añadir una comprobación? Todas las reglas están en `src/CHC.Core/Health/HealthAnalyzer.cs` y los textos, con las mismas claves, en los archivos de idioma.

## Traducciones

Todos los textos están en `src/CHC.Core/Localization/Languages/<código>.json`. Para añadir un idioma:

1. Copia `en.json` como `<código>.json` (por ejemplo, `it.json`).
2. Traduce los valores y deja sin cambios las claves y los marcadores como `{0}`.
3. Abre un pull request o coloca el archivo en la carpeta `Languages` junto a `CHC.exe`. Aparecerá en **Configuración → Idioma** tras reiniciar.

Consulta [CONTRIBUTING.md](CONTRIBUTING.md) para más detalles.

## Licencia

CHC se publica bajo la **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ Puedes usar, estudiar, modificar y compartir CHC gratis para fines personales y otros fines no comerciales.
- ❌ No puedes vender CHC, distribuirlo a cambio de dinero ni usarlo con fines comerciales.

Los componentes de terceros mantienen sus propias licencias: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Aviso

La puntuación de salud se basa en los datos que informa tu hardware y en reglas prácticas habituales. Es una orientación útil, no una garantía. Haz siempre copias de seguridad de tus datos importantes.
