# Arquitectura pública

Este documento describe únicamente la arquitectura de alto nivel de **Win11 Performance Control Center**.

## Capas

~~~mermaid
flowchart LR
    A["WPF host"] --> B["WebView2 UI"]
    A --> C["Bridge tipado"]
    C --> D["Catálogo de acciones"]
    D --> E["Ejecución controlada"]
    E --> F["Diagnóstico"]
    E --> G["Operaciones protegidas"]
    G --> H["Elevación UAC puntual"]
    E --> I["Estado y rollback"]
~~~

La versión completa incorpora contratos, validaciones, mecanismos de recuperación y detalles de implementación que no se publican en esta edición.

## Objetivos de diseño

- mantener la interfaz separada de la ejecución del sistema;
- evitar comandos arbitrarios;
- elevar privilegios sólo para acciones concretas;
- distinguir lectura, escritura y recovery;
- registrar suficiente contexto para verificar resultados.

## Portfolio Edition

Este repositorio no pretende ser una distribución compilable del producto completo. Su objetivo es documentar el enfoque técnico y mostrar el producto sin exponer el núcleo comercial.
