<div align="center">

# 🖥️ Win11 Performance Control Center

### Public Portfolio Edition

Aplicación de escritorio para diagnóstico, mantenimiento controlado y análisis de Windows 11.

![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)
![WPF](https://img.shields.io/badge/WPF-Desktop-0C54C2?style=flat-square)
![WebView2](https://img.shields.io/badge/WebView2-Edge-0A84FF?style=flat-square)
![TypeScript](https://img.shields.io/badge/TypeScript-Frontend-3178C6?style=flat-square)
[![Pages](https://github.com/truquinio/win11-performance-control-center/actions/workflows/pages.yml/badge.svg)](https://github.com/truquinio/win11-performance-control-center/actions/workflows/pages.yml)

[**Portfolio demo**](https://truquinio.github.io/win11-performance-control-center/) ·
[**Arquitectura**](docs/ARCHITECTURE.md) ·
[**Seguridad**](docs/SECURITY_OVERVIEW.md)

</div>

---

> [!IMPORTANT]
> Este repositorio es una **edición pública de portfolio**. Muestra el producto, su diseño y su arquitectura de alto nivel, pero **no contiene el núcleo comercial completo**, la implementación real de acciones privilegiadas, automatizaciones internas, recovery avanzado ni packaging de producción.

## 👀 Qué muestra

Win11 Performance Control Center fue diseñado como una herramienta local para concentrar diagnóstico, métricas y acciones controladas sobre Windows 11.

El flujo conceptual es:

> **detectar → diagnosticar → medir → explicar → actuar → verificar → revertir**

<p align="center">
  <img src="docs/screenshots/dashboard.png" alt="Dashboard de Win11 Performance Control Center" width="96%"/>
</p>

La captura utiliza datos de demostración y no expone información de un equipo real.

## ✨ Áreas funcionales

- CPU, procesos y EcoQoS;
- presión de memoria y MemoryTrim;
- almacenamiento y limpieza segura;
- integridad de Windows;
- red, energía y temperatura;
- drivers, aplicaciones e inicio;
- Windows Update;
- navegadores y extensiones;
- multimedia;
- privacidad y activación;
- reliability y recovery.

## 🏗️ Arquitectura pública

~~~mermaid
flowchart TD
    UI["WPF + WebView2"] --> BR["Bridge tipado"]
    BR --> CAT["Catálogo de acciones"]
    CAT --> EX["Capa de ejecución"]
    EX --> RO["Diagnóstico read-only"]
    EX --> PR["Operaciones protegidas"]
    PR --> UAC["Elevación puntual"]
    EX --> ST["Estado / rollback"]
~~~

La edición completa desarrolla estas capas con controles adicionales, contratos internos y mecanismos de recuperación que no forman parte de este showcase.

📘 [Ver arquitectura pública](docs/ARCHITECTURE.md)

## 🔐 Principios de seguridad

- funcionamiento local;
- elevación sólo cuando una operación lo requiere;
- acciones identificadas y controladas;
- sin ejecución arbitraria desde la interfaz;
- confirmación para operaciones sensibles;
- separación entre diagnóstico y modificación;
- rollback cuando la operación lo permite.

📘 [Resumen de seguridad](docs/SECURITY_OVERVIEW.md)

## 🧩 Stack

**Desktop:** C# · WPF · .NET 10  
**UI:** HTML · CSS · TypeScript · WebView2  
**Windows:** WMI · Event Log · Registry · Windows APIs  
**Calidad del proyecto completo:** tests .NET · contratos frontend · UI smoke · CI

## 📦 Alcance de esta edición

### Incluido

- documentación pública;
- captura real del producto;
- arquitectura de alto nivel;
- modelo conceptual de seguridad;
- landing estática de portfolio.

### No incluido

- código fuente completo;
- implementación real del Action Catalog;
- acciones privilegiadas;
- automatizaciones y optimizaciones internas;
- recovery/rollback completo;
- tests y herramientas internas;
- binarios y packaging comercial.

La omisión es deliberada: este repositorio existe para **demostrar el trabajo sin publicar la implementación comercial completa**.

## 📌 Estado

**Portfolio / Showcase Edition.**  
El desarrollo completo continúa de forma privada.

## 🔏 Uso y reutilización

Este repositorio público muestra el proyecto con fines de portfolio. **No concede una licencia open source de reutilización del código**.

---

© 2026 Federico Trucco. All rights reserved.  
**by [truquinio](https://github.com/truquinio)** · [LinkedIn](https://www.linkedin.com/in/federico-trucco/)
