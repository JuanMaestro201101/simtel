# APEX

## Para probadores

1. **Descarga:** [última versión](https://github.com/JuanMaestro201101/simtel/releases/latest) → `APEX-windows.zip` (Windows 10/11 de 64 bits; no hay que instalar nada más).
2. **Instala:** extrae el zip en una carpeta y abre `APEX.exe` (si Windows avisa: «Más información» → «Ejecutar de todas formas»).
3. **Aprende a usarla:** **[tutorial completo con capturas](docs/TUTORIAL.md)**: configurar cada juego, volante o mando, y qué hace cada pestaña.
4. **¿Algo falla?** Abre un [Issue](https://github.com/JuanMaestro201101/simtel/issues/new) con el registro de diagnóstico adjunto.

App de Windows para grabar y analizar la telemetría de juegos de carreras. Incluye un overlay de acelerador y freno que se dibuja encima del juego, un entrenador de pedales y una guía de técnica.

## Ejecutar

- **Ejecutable:** `dist\SimTelemetry.exe`. Necesita el *.NET 8 Desktop Runtime*, que ya está instalado en este PC.
- **Compilar desde el código:** `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist`

La primera vez que cierras la ventana, la app pregunta qué hacer:
- **Seguir en segundo plano:** queda en la bandeja del sistema y sigue grabando y mostrando el overlay.
- **Cerrarse del todo:** se cierra también el overlay.

## Volante o mando

En pista los datos (pedales, volante, velocidad) los envía el juego, así que da igual con qué conduzcas. Fuera del juego (Entrenador, overlay sin juego) la app lee volantes y pedaleras USB con DirectInput y los mandos tipo Xbox con XInput: con mando, el gatillo LT es el freno y el RT el acelerador (DirectInput junta los dos gatillos en un solo eje, por eso se leen con XInput).

## Juegos compatibles

| Juego | Cómo se lee | ¿Hay que configurar algo? |
|---|---|---|
| DiRT Rally, DiRT Rally 2.0, DiRT 4 | UDP 20777 (extradata 3) | Sí: botón en *Juegos*, **con el juego cerrado** |
| EA SPORTS WRC | UDP 20790 (estructura propia `simtelemetry`, con nombre del tramo, del rally y del coche) | Sí: botón en *Juegos*, **con el juego cerrado** (vuelve a pulsarlo si la app avisa de una versión antigua) |
| Forza Motorsport (2023) / 7, Forza Horizon 4 / 5 | UDP 5300 (formato *Car Dash*, en español «Aceleración de auto»; no «Trineo») | Sí: *Data Out* en el juego y, en la versión de Game Pass / Microsoft Store, el botón «Dar permiso de red a Forza» |
| Assetto Corsa Competizione, Assetto Corsa | Memoria compartida | No. En ACC la longitud sale de la biblioteca de circuitos (o se mide sola en tu primera vuelta completa) y también se leen las presiones de los neumáticos |
| iRacing | SDK oficial (memoria compartida) | No |
| BeamNG.drive | OutGauge + MotionSim, los dos al UDP 4444 (MotionSim da la posición: mapa y vueltas detectadas al volver al punto de salida) | Sí: *Opciones → Otros → Protocolos*, activar los dos con IP 127.0.0.1 |
| Live for Speed | OutGauge UDP 4444 (sin posición: recorrido continuo) | Sí, en `cfg.txt` |

La app escucha todos los juegos a la vez y usa el que esté enviando datos. Si un juego compatible está abierto pero no envía telemetría, *En vivo* lo dice y explica qué activar. Si otro programa (SimHub…) ocupa un puerto, la app lo avisa y vuelve a intentarlo cada 10 s. Si un juego está en pista y en la pestaña *En vivo* su contador de paquetes no sube, el juego no está enviando datos. En *Juegos → Comprobar* verás la causa: configuración, permiso o falta reiniciar el juego.

Lo que ocurre (conexiones, cortes de vuelta, errores) queda en `%APPDATA%\SimTelemetry\log.txt`. Se abre desde *Juegos → Abrir registro de diagnóstico*.

> Si abres la app desde otra aplicación empaquetada de Windows (por ejemplo, un enlace en Claude Desktop), Windows guarda sus ajustes y su registro en una copia aparte, dentro de `%LOCALAPPDATA%\Packages\…\LocalCache\Roaming\SimTelemetry`. Ábrela siempre desde el Explorador o desde un acceso directo.

## Qué hace

La ventana tiene una barra de pestañas arriba, en cuatro grupos: Conducir (En vivo, Ingeniero), Analizar (Análisis, Progreso, Circuitos, Coches), Practicar (Entrenador, Aprender) y Ajustes (Overlay, Juegos, Apariencia).

**Apariencia:** tema oscuro o claro y un color (naranja, rojo, rosa, morado, azul, turquesa, verde o el amarillo original).
- El color se usa en botones, casillas, deslizadores y lo seleccionado, y el fondo de la ventana toma un tono de ese color.
- Los colores con significado no cambian: verde acelerador, rojo freno, ámbar aviso. Tampoco cambia el overlay sobre el juego.
- Con el tema claro, las gráficas usan tonos más oscuros de los mismos colores para leerse sobre blanco; una prueba automática comprueba el contraste de todas las combinaciones.

**Perfiles por juego:** arriba, en *Perfil*, eliges «Todos» o un juego (ACC, Assetto Corsa, iRacing, Forza, DiRT Rally, EA WRC…).
- Con un perfil, *Análisis*, *Progreso* y *Circuitos* sólo muestran ese juego.
- En ACC y AC la biblioteca muestra sólo sus circuitos oficiales; en rally no hay biblioteca de circuitos.
- Con «Cambiar solo al abrir un juego» (activado por defecto), el perfil cambia en cuanto abres un juego o empieza a enviar datos.
- Si eliges otro perfil a mano, se respeta hasta que abras otro juego.
- La app recuerda el último perfil al abrirla.

- **En vivo:** muestra el juego detectado, la velocidad, la marcha, las RPM, el tiempo de vuelta y una vista previa del overlay.
- **Sesión actual y tarjeta de información:** al entrar en una sesión la app reconoce el juego, el circuito o tramo, el coche y su categoría (GT3, GT4, GT2, Cup… en ACC; Rally2, WRC2… en EA WRC). Te recomienda con qué coche de esa categoría eres más rápido ahí, según tus propios tiempos.
  - Se ve en *En vivo → Sesión actual* y en una tarjeta pequeña sobre el juego, como la de Steam. La tarjeta aparece sola unos segundos al entrar y mientras estás en boxes.
  - Con **Ctrl+Tab** (o Mayús+Tab, Bloq Despl, F8, F9, F10, Inicio) se amplía con la información del circuito: curvas clave, reglaje, qué coche conviene, dónde adelantar y la superficie del rally en EA WRC. Otra pulsación la oculta. El juego también recibe las teclas; Mayús+Tab abre el overlay de Steam en los juegos de Steam.
  - Los juegos sólo dicen el circuito y el coche cuando la sesión ya está cargada (en boxes o en pista), no mientras navegas por los menús.
- **Grabación automática:** guarda cada vuelta o tramo en `Documentos\SimTelemetry\<juego>\<fecha - circuito>\lap_NNN.csv` (una carpeta por juego; las sesiones antiguas quedan donde estaban).
  - Son CSV normales que se abren también en Excel.
  - La meta se detecta por la posición en la vuelta y el tiempo es el oficial del juego.
  - Una vuelta se marca como *incompleta* si empezó a mitad de camino o saliendo de boxes, y como *inválida* si el juego la invalidó por límites de pista.
- **Primeros pasos:** en *En vivo*, una tarjeta con lo que falta (conectar un juego, detectar los pedales, primera vuelta completa, probar el sonido del ingeniero), con un botón para cada cosa. Se oculta sola al terminar.
- **Diferencia en vivo** con tu mejor vuelta, en el overlay y en *En vivo*: verde si vas más rápido, rojo si vas más lento. Al salir de cada curva aparece unos segundos lo que ganaste o perdiste en ella («C3 -0.12»), medido igual que en el análisis: de su frenada a la siguiente.
- **Pantalla en el teléfono** (*Overlay → Pantalla en el teléfono*): el teléfono abre una página en el navegador por la Wi-Fi de casa, sin instalar nada (escanea el QR).
  - Muestra marcha, velocidad, barra de RPM con colores de cambio, gráfico de pedales, diferencia en vivo, la última curva, tiempos de vuelta y la próxima frenada que te canta el Ingeniero.
  - Los datos llegan 20 veces por segundo.
  - La dirección lleva un código secreto, la página sólo muestra datos y está desactivada por defecto. Al activarla, Windows pregunta una vez por el cortafuegos.
- **Datos fiables:** cada vuelta pasa un control de calidad al guardarse. Si hay cortes de datos, rebobinados, saltos de posición, una salida desde parado (en circuito) o una distancia que no cuadra con la velocidad, se guarda con la etiqueta «no sirve de referencia» y la razón. Así no se usa para el Ingeniero, la vuelta ideal, el margen de frenada ni la recomendación de coche. Las pausas a mitad de vuelta se descuentan del tiempo.
- **Análisis:**
  - filtros por juego, circuito o tramo y categoría del coche, para no mezclar sesiones;
  - **«Qué mejorar» en Resumen o Detalle:** cada problema dice qué pasa (con tus números), por qué pasa, qué hacer y qué practicar en pista, con un botón «Practicar» que abre el ejercicio adecuado del Entrenador. En Progreso, las tendencias que empeoran también traen su ejercicio.
  - **«Qué mejorar» por curva** (con la misma numeración que el Ingeniero y Circuitos): cada curva cuenta desde su frenada hasta la siguiente y explica la cadena de causas, por ejemplo «frenas 12 metros antes → pasas el vértice 6 km/h más lento → sales 5 km/h más lento → la recta siguiente la haces 8 km/h más lento»;
  - gráficas apiladas de velocidad, diferencia de tiempo, pedales, volante, marcha y RPM, que se pueden ampliar y mover;
  - **mapa de la trazada** (ACC, DiRT Rally, EA WRC, Forza): tu línea frente a la de referencia, en verde donde ganas tiempo y en rojo donde lo pierdes, con los puntos de frenada;
  - **«Qué mejorar»:** en qué tramos pierdes tiempo y por qué, más consejos de técnica;
  - **«Frenadas»:** cada frenada puntuada de 0 a 100 frente a los parámetros ideales (ataque, presión máxima según tenga ABS o no, trail braking, suelta brusca).
- **Progreso:** tu evolución en cada circuito o tramo a lo largo de las sesiones: tiempos, consistencia y técnica.
- **Circuitos:** una guía de cada circuito o tramo.
  - Lo que aprende de tus vueltas: mapa de tu mejor vuelta con las curvas numeradas, el punto de frenada de cada curva (metros desde meta, metros antes del vértice, intensidad, velocidades y marcha), tu constancia en cada punto, tu **vuelta ideal** (tus mejores tramos juntos) y **con qué coche eres más rápido** en ese circuito.
  - **Margen de frenada:** la app mide cuánto frena de verdad tu coche en recta con el freno a fondo, según la velocidad: en tus vueltas de ACC, un GT3 da ~1,4 G a 120 km/h y ~1,9 G a 220 km/h. Con eso calcula cuántos metros más tarde podrías empezar a frenar en cada curva para llegar igual de lento al punto de giro.
  - La biblioteca: 29 circuitos de ACC, Assetto Corsa, iRacing y Forza, con su carácter, curvas clave, dónde adelantar, reglaje recomendado, qué coche conviene y el ritmo orientativo de un piloto rápido en ACC.
  - Los circuitos que el juego no nombra (Forza da sólo un número) se pueden asociar a uno de la biblioteca.
- **Ingeniero de carrera:** te habla por los auriculares mientras conduces, con la voz en español de Windows.
  - Antes de cada frenada te dice cuánto frenar, con qué presión de pedal (de 5 en 5 %) y la marcha de la curva: «Frena fuerte, 90 por ciento, segunda».
  - **Según el juego:**
    - en rally (DiRT Rally, EA WRC) sólo suenan los pitidos en las curvas, porque el copiloto ya canta las notas y las voces se pisarían;
    - si activas la voz en rally, los avisos son cortos («Fuerte 90»);
    - el resumen de vuelta, o del tramo, y el resultado del modo práctica no se dicen a punto de frenar: esperan a una recta con tiempo para decirlos enteros antes del siguiente aviso.
  - **Sincronizado con el juego:**
    - cada frase empieza para terminar justo antes del primer pitido; si la recta es corta, dice una versión más corta, y si no cabe ninguna, se calla, porque un aviso tarde confunde;
    - los pitidos van en su propio hilo, con prioridad alta, y nunca esperan detrás de la voz;
    - ACC se lee a 100 Hz con el reloj del sistema a 1 ms;
    - el «adelanto de los avisos» compensa el retraso de tus auriculares: ~80 ms con cable y ~250 ms con Bluetooth. Luego suenan dos pitidos cortos y uno largo justo donde frenaste en tu mejor vuelta con ese coche.
  - Al cruzar la meta: tiempo de la vuelta, diferencia con tu mejor vuelta y la curva donde más pierdes, con la causa (frenas antes, pasas más lento…).
  - En la vuelta siguiente, al llegar a la curva donde más perdiste, te lo recuerda antes del aviso: «Curva 5: lleva más velocidad al vértice. Frena fuerte…».
  - **Modo práctica de una curva:** en *Ingeniero → Plan de frenadas* pulsa «Practicar» en una curva. Sólo te avisará de esa curva, y al salir de ella te dirá cuánto ganaste o perdiste frente a tu referencia y si es tu mejor intento.
  - **Objetivo progresivo** (activado por defecto): en las curvas donde eres constante (al menos 3 vueltas buenas, ±12 m), el aviso llega unos metros más tarde que tu mejor vuelta: la mitad del margen que permite tu coche, como mucho 15 m. Cuando lo consigues, tu nueva mejor vuelta trae el siguiente paso.
  - En ACC vigila las presiones en caliente (ventana de 26,0–27,0 psi en seco) y te dice cuánto subir o bajar en frío, y te avisa del combustible que queda (cada 5 vueltas, o siempre que queden menos de 4).
  - Se activa, se prueba y se ajusta el volumen en la pestaña *Ingeniero*.
  - **Voces:** puedes elegir cualquier voz instalada en Windows, por ejemplo Raúl o Sabina (México), y ajustar su velocidad y su tono.
    - Las modernas suenan más naturales, y con ellas los avisos de cada curva se preparan al cargar la referencia, así que suenan al instante.
    - La app recorta el silencio del audio y usa su duración exacta para que cada frase termine antes del primer pitido.
    - Para más voces en español, por ejemplo de España, pulsa «Instalar más voces» y luego «Actualizar lista». Funciona con todos los juegos que envían la distancia recorrida; en rally hace de notas de frenada del tramo que ya corriste.
- **Volante y pedales sin juego:** la app detecta sola el volante o la pedalera USB con DirectInput (también al conectarlo o desconectarlo) y lo lee aunque no haya ningún juego abierto.
  - La primera vez, en *Entrenador → Detectar pedales*, basta con pisar el freno, el acelerador y el embrague cuando te lo pida, sin tocar el ratón.
  - En modo *Automático* se usan los pedales del juego mientras estás en pista y los del volante en cualquier otro momento; así funcionan el entrenador, la vista *En vivo* y el overlay.
- **Sesiones de pedales sin telemetría:** si juegas a un juego compatible que no envía datos (por ejemplo Forza sin *Data Out*), la app graba los pedales y el giro del volante en tramos de hasta 10 minutos.
  - En *Análisis* se ven por tiempo, con cada frenada puntuada (ataque, presión, trail braking, suelta brusca).
  - No hay vueltas, circuito ni velocidad: esos datos sólo los envía el juego.
- **Entrenador de pedales:** seis ejercicios con puntuación y récord: presión exacta, frenada de umbral 80 % × 10, trail braking, acelerador progresivo, acelerador parcial y reflejos.
  - **Tacto como en el juego:** cada juego convierte el pedal a su manera (saturación, curva, zona muerta), así que el mismo recorrido no da el mismo porcentaje.
  - Mientras conduces con el volante, la app compara la posición real del pedal con lo que envía el juego y mide su curva sola.
  - En *Entrenador → Tacto de los pedales* eliges el juego, y los ejercicios usan ese mismo tacto; por ejemplo: «en ACC el freno llega al 100 % con el 79 % del recorrido».
- **Aprender:** parámetros ideales de frenada, trail braking, línea de carrera (vértice tardío o temprano, curvas enlazadas), acelerador, rally (freno con el pie izquierdo, freno de mano, Scandinavian flick), ajustes de pedales, **reglaje según lo que hace el coche y según el circuito y las condiciones** (lluvia, calor, pianos, carrera larga) y un plan de práctica. Incluye enlaces a las fuentes.
- **Overlay:**
  - una gráfica con los últimos segundos de acelerador y freno, una columna por pedal (embrague opcional) y la marcha con la velocidad;
  - es transparente a los clics: mientras juegas no se puede tocar;
  - por defecto **sólo se muestra mientras conduces**;
  - para colocarlo, entra en *Overlay → Desbloquear* (o en *Mover / redimensionar overlay*, en el menú del icono de la bandeja):
    - arrástralo desde el centro para moverlo a cualquier sitio, también a otro monitor;
    - arrastra los bordes o las esquinas para cambiar el tamaño;
    - la rueda del ratón lo agranda o lo achica;
    - las flechas lo ajustan píxel a píxel;
    - también se puede poner la posición y el tamaño exactos.

> El overlay sólo se ve si el juego está en **ventana sin bordes / pantalla completa en ventana**. En pantalla completa exclusiva Windows no deja dibujar encima.

> **Rendimiento con el juego delante:** el overlay se dibuja con la CPU a 30 fotogramas por segundo, para no competir con el juego por la tarjeta gráfica. La app pide a Windows prioridad «superior a la normal» y que no la frene en segundo plano (EcoQoS / Modo Juego). Con el overlay animándose usa alrededor del 4 % de un núcleo.

## Probar sin juego

```bash
python tools/simulador.py --juego forza --vueltas 3 --rapidez 8
python tools/simulador.py --juego dirt --rapidez 8
python tools/simulador.py --juego wrc --rapidez 8
python tools/simulador.py --juego forza --vueltas 4 --rapidez 8 --nivel 0.8
```

- `--nivel` (de 0 a 1) hace que el piloto simulado conduzca mejor, para ver el progreso entre sesiones. Deja unos segundos entre una ejecución y otra.
- Para no mezclar las pruebas con la app que tengas abierta: `SIMTELEMETRY_SETTINGS=<ajustes.json>`, `SIMTELEMETRY_LOG=<log.txt>` y `--data <carpeta>`. Pon otros puertos en esos ajustes y usa `--puerto` en el simulador.
- `--hidden` dibuja todo fuera de la pantalla: ni la ventana, ni el overlay, ni la tarjeta aparecen, no le quitan el foco al juego y no hay icono en la bandeja. Sirve para probar mientras alguien juega.
- `SimTelemetry.exe --selftest` (Debug) comprueba la biblioteca de circuitos, la lectura de vueltas, la vuelta ideal y las frases del ingeniero, y lo escribe en el registro.
- `--juego acc` imita la memoria compartida de ACC. Necesita la versión Debug de la app arrancada con la variable `SIMTELEMETRY_FORCE_ACC=1`.

## Estructura del código

- `Telemetry/`: lectores de cada juego (`Sources/`), `TelemetryHub`, que decide qué juego está activo, y `TrackLengthStore`, con las longitudes de circuito medidas.
- `Recording/`: corte en vueltas o tramos (`LapRecorder`) y formato CSV (`LapFile`).
- `Remote/`: pantalla del teléfono (`PhoneServer`: servidor web mínimo con eventos en vivo; `PhonePage`: la página).
- `Recording/LapQuality.cs`: control de calidad de cada vuelta.
- `Engineer/`: ingeniero de carrera (`RaceEngineer`: avisos de frenada, resumen de vuelta y presiones) y su audio (`EngineerAudio`: voz con System.Speech y pitidos).
- `Tracks/TrackDatabase.cs`: biblioteca de circuitos. `Analysis/TrackGuide.cs`: lo aprendido de tus vueltas en cada circuito.
- `Analysis/LapAnalyzer.cs`: métricas, zonas de frenada puntuadas (`BrakeIdeals`), diferencia por distancia y consejos.
- `Analysis/ProgressAnalyzer.cs`: resumen por sesión y tendencias en un mismo circuito o tramo.
- `Training/PedalExercise.cs`: ejercicios del entrenador.
- `Input/WheelInput.cs` e `Input/PedalDetector.cs`: lectura del volante con DirectInput (paquete NuGet `Vortice.DirectInput`) y detección de pedales sin ratón.
- `Setup/GameConfigurator.cs`: configuración y comprobaciones de DiRT, EA WRC y Forza (guarda una copia `.simtelemetry.bak`).
- `Diagnostics/Log.cs`: registro de diagnóstico.
- `UI/`: overlay, gráfica de análisis, mapa de la trazada, pantalla del entrenador y contenido de la guía.
