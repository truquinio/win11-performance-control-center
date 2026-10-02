<p align="center">
  <img src="src/App/Assets/app-icon.png" width="112" alt="Win11 Performance Control Center"/>
</p>

<h1 align="center">Win11 Performance Control Center</h1>

<p align="center">
  Diagnóstico, mantenimiento seguro y optimización verificable para Windows 11.
  <br/>
  <strong>Offline-first · allowlisted actions · UAC puntual · rollback donde corresponde</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-11-0078D4?style=flat&logo=windows11&logoColor=white" alt="Windows 11"/>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat&logo=dotnet&logoColor=white" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/WPF-Desktop-0C54C2?style=flat" alt="WPF"/>
  <img src="https://img.shields.io/badge/WebView2-Edge-0A84FF?style=flat&logo=microsoftedge&logoColor=white" alt="WebView2"/>
  <img src="https://img.shields.io/badge/TypeScript-Frontend-3178C6?style=flat&logo=typescript&logoColor=white" alt="TypeScript"/>
  <img src="https://img.shields.io/badge/tests-70%2F70-brightgreen?style=flat" alt="70/70 tests"/>
</p>

<p align="center">
  <strong>by truquinio</strong>
</p>

---

## 🖥️ Vista general

Win11 Performance Control Center centraliza métricas, diagnóstico y tareas de mantenimiento de Windows 11 en una aplicación local. El objetivo no es aplicar tweaks indiscriminados, sino seguir un flujo controlado:

`detectar → diagnosticar → medir → explicar → actuar → verificar → revertir`

La interfaz ofrece un modo **Compact** para uso diario y un modo **Developer** con módulos técnicos, Action Catalog, evidencias y controles avanzados.

### Compact

<p align="center">
  <img src="docs/screenshots/dashboard-compact.png" alt="Dashboard Compact" width="100%"/>
</p>

### Developer

<p align="center">
  <img src="docs/screenshots/dashboard-developer.png" alt="Dashboard Developer" width="100%"/>
</p>

> Las capturas utilizan el modo DEMO con datos simulados; no exponen información del equipo real.

## ⚡ Funcionalidad

- 🧠 **CPU / EcoQoS** — análisis de procesos, selección segura, aplicación y rollback.
- 🧩 **RAM / MemoryTrim** — presión de memoria, preview de candidatos y recorte seleccionado.
- 💾 **Almacenamiento** — espacio disponible, categorías recuperables y Safe Cleanup en dry-run.
- 🛡️ **Integridad** — línea base del sistema y DISM CheckHealth con UAC puntual.
- 🌐 **Red** — adaptador activo, enlace y tráfico medido.
- 🌡️ **Energía y temperatura** — plan de energía, ACPI y lectura NVIDIA cuando está disponible.
- 🧰 **Drivers, aplicaciones e inicio** — auditorías locales read-only.
- 🔄 **Windows Update** — análisis de eventos recientes de instalación y error.
- 🌍 **Navegadores** — inventario y diagnóstico de salud de extensiones de Edge.
- 🎧 **Multimedia** — inventario de dispositivos de audio y vídeo.
- 🔐 **Privacidad y activación** — lectura de configuraciones y estado de licencia.
- 📈 **Reliability** — correlación de reinicios, apagados inesperados y eventos relevantes.
- ↩️ **Recovery / rollback** — detección de operaciones incompletas y snapshots de EcoQoS.

## 🔐 Modelo de seguridad

La aplicación está diseñada para que la UI no pueda ejecutar comandos arbitrarios.

- El frontend sólo invoca **Action IDs tipadas y allowlisted**.
- Las acciones WRITE requieren parámetros estructurados y confirmación explícita.
- El proceso funciona sin elevación por defecto.
- UAC se solicita únicamente para acciones concretas que lo necesitan.
- El helper elevado vuelve a ejecutar el **mismo ejecutable**, valida Action ID y utiliza un token efímero de respuesta.
- No desactiva Windows Security.
- No modifica BIOS, firmware ni drivers.
- No reinicia Windows automáticamente.
- Safe Cleanup permanece en **DRY_RUN** en la versión actual.

Más detalle en [docs/SECURITY.md](docs/SECURITY.md).

## 🏗️ Arquitectura

```text
WPF / .NET 10
    │
    ├── WebView2
    │     └── HTML + CSS + TypeScript
    │
    ├── HostBridge (IPC tipado)
    │     └── ActionCatalog
    │           └── ActionExecutor
    │                 ├── Servicios read-only
    │                 ├── Operaciones protegidas
    │                 └── ElevatedActionClient (UAC puntual)
    │
    └── Logs / state / rollback local
```

| Capa | Tecnología |
| --- | --- |
| Desktop host | C# · WPF · .NET 10 |
| UI | HTML · CSS · TypeScript |
| Render | Microsoft Edge WebView2 |
| Sistema | WMI · Event Log · Registry · Windows APIs |
| Tests | xUnit · Node UI smoke · contract tests |
| CI | GitHub Actions |

Documentación técnica: [Architecture](docs/ARCHITECTURE.md) · [Action Catalog](docs/ACTION_CATALOG.md) · [Testing](docs/TESTING.md) · [Demo](docs/DEMO.md)

## ✅ Calidad y verificación

La puerta de verificación actual comprueba:

- **70/70 tests** .NET.
- Release build con **0 warnings / 0 errors**.
- Analyzers y code-style gates.
- Contrato frontend: **23 views**, **18 rutas Developer**, **40 botones literales**.
- Paridad entre el Action Catalog TypeScript y el catálogo C#.
- UI smoke de navegación, iconos, botones y feedback.
- Auditoría NuGet: **0 paquetes vulnerables conocidos**.
- Auditoría npm de producción: **0 vulnerabilidades**.

Ejecutar la suite completa:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

## 🚀 Build local

### Requisitos

- Windows 11 x64
- .NET 10 SDK / Runtime
- Microsoft Edge WebView2 Runtime
- Node.js + npm para compilar/verificar el frontend

### Compilar y empaquetar

```powershell
npm ci
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\build-local.ps1
```

Salida:

```text
artifacts/local-win-x64/
artifacts/Win11PerformanceControlCenter-win-x64.zip
```

### Demo estática

```powershell
npm run build:demo
```

La demo usa exactamente el mismo frontend, con datos simulados y sin acceso al sistema operativo.

## 📁 Estructura

```text
src/
├── App/                 # WPF host, bridge, services, action execution
└── Frontend/            # UI HTML/CSS/TypeScript + iconos

tests/App.Tests/         # unit + integration tests
scripts/                 # build, contract, UI smoke, verification
docs/                    # arquitectura, seguridad y screenshots
.github/workflows/       # CI
```

## 👤 Autor

**truquinio**

[![GitHub](https://img.shields.io/badge/GitHub-truquinio-181717?style=flat&logo=github&logoColor=white)](https://github.com/truquinio)
[![LinkedIn](https://img.shields.io/badge/LinkedIn-Federico%20Trucco-0077B5?style=flat&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/federico-trucco/)

---

<p align="center">
  <sub>Win11 Performance Control Center · v0.1.0 · by truquinio</sub>
</p>
