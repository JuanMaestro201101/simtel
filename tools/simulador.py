"""
Simulador de telemetría para probar SimTelemetry sin abrir ningún juego.

Envía paquetes con el mismo formato que los juegos reales:
    python simulador.py --juego forza --vueltas 3      # circuito (Forza Motorsport 2023, UDP)
    python simulador.py --juego acc --vueltas 3        # circuito (ACC, memoria compartida)
    python simulador.py --juego dirt                   # tramo de rally (DiRT Rally 2.0, UDP)
    python simulador.py --juego wrc                    # tramo de rally (EA SPORTS WRC, UDP)
    python simulador.py --juego beamng --vueltas 3     # circuito (BeamNG.drive: OutGauge + MotionSim al mismo puerto UDP)

--rapidez N acelera el envío N veces (el tiempo del "juego" sigue siendo real).
--nivel 0..1 hace al piloto mejor (frena más tarde, menos inercia): úsalo para simular progreso.

Para --juego acc, la app (versión Debug) debe arrancarse con la variable SIMTELEMETRY_FORCE_ACC=1.
"""
import argparse
import math
import mmap
import socket
import struct
import time

HZ = 60
DT = 1.0 / HZ


def corners_for(kind):
    # (distancia del vértice en m, velocidad mínima km/h, sentido: 1 derecha / -1 izquierda)
    if kind == "circuito":
        return 2400.0, [(450, 95, 1), (900, 60, -1), (1450, 120, 1), (1900, 75, 1), (2250, 140, -1)]
    return 3000.0, [(400, 85, -1), (950, 55, 1), (1500, 100, -1), (2050, 70, 1), (2600, 110, -1)]


def position(d, length, lateral):
    """Posición (x, z) en un circuito cerrado con forma de alubia; 'lateral' desplaza la trazada."""
    th = 2 * math.pi * (d % length) / length
    r = length / (2 * math.pi) * (1 + 0.25 * math.cos(2 * th))
    r += lateral
    return r * math.cos(th), r * math.sin(th) * 0.7


class Driver:
    """Piloto simple: frena antes de cada curva, mantiene en el vértice y acelera a la salida.
    Cada vuelta varía un poco (frena antes, deja inercia...) para que el análisis tenga algo que decir."""

    def __init__(self, length, corners, circuit, lap_style):
        self.length, self.corners, self.circuit = length, corners, circuit
        self.style = lap_style
        self.brake = 0.0

    def next_corner(self, d):
        best = None
        for cd, vmin, side in self.corners:
            ahead = cd - d
            if self.circuit and ahead < -40:
                ahead += self.length
            if ahead >= -40 and (best is None or ahead < best[0]):
                best = (ahead, cd, vmin, side)
        return best

    def controls(self, d, v):
        thr, target_brake, steer = 1.0, 0.0, 0.0
        nc = self.next_corner(d)
        if nc:
            ahead, cd, vmin, side = nc
            vmin_ms = vmin / 3.6 - self.style["slower_apex"] / 3.6
            decel = 13.0 * self.style["brake_force"]
            needed = max(0.0, (v * v - vmin_ms * vmin_ms) / (2 * decel)) + self.style["early_brake"]
            steer = side * 0.75 * math.exp(-(ahead / 45.0) ** 2)
            if 0 < ahead <= needed and v > vmin_ms + 0.5:
                thr, target_brake = 0.0, self.style["brake_force"]
                # Trail braking: el piloto bueno sigue frenando un poco al girar.
                if self.style.get("trail") and ahead < 40:
                    target_brake = self.style["brake_force"] * max(0.3, ahead / 40)
            elif -40 <= ahead <= 25:
                thr = 0.35
            elif -40 - self.style["coast"] <= ahead < -40:
                thr = 0.0  # inercia a la salida
        # El pedal de freno no es instantáneo.
        rate = DT / self.style["brake_ramp"]
        self.brake += max(-rate * 2, min(rate, target_brake - self.brake))
        self.brake = max(0.0, self.brake)
        return thr, self.brake, steer


def gear_rpm(kmh):
    limits = [0, 50, 85, 120, 155, 190, 230]
    g = 1
    while g < 6 and kmh > limits[g]:
        g += 1
    lo, hi = limits[g - 1], limits[g]
    rpm = 2500 + (kmh - lo) / max(1, hi - lo) * 5000
    return g, min(rpm, 7800)


class AccMemory:
    """Imita las tres páginas de memoria compartida de ACC (mismos desplazamientos que la app)."""

    def __init__(self):
        self.phys = mmap.mmap(-1, 800, tagname="Local\\acpmf_physics")
        self.gfx = mmap.mmap(-1, 1600, tagname="Local\\acpmf_graphics")
        self.sta = mmap.mmap(-1, 800, tagname="Local\\acpmf_static")
        self.packet = 0
        self._wstr(self.sta, 68, "sim_gt3", 33)
        self._wstr(self.sta, 134, "sim_ring", 33)
        struct.pack_into("<i", self.sta, 412, 8000)
        struct.pack_into("<f", self.sta, 520, 0.0)  # ACC no publica la longitud del circuito

    @staticmethod
    def _wstr(buf, off, text, chars):
        data = text.encode("utf-16-le")[: (chars - 1) * 2]
        buf[off:off + chars * 2] = data + b"\0" * (chars * 2 - len(data))

    def write(self, thr, brk, steer, gear, rpm, kmh, lat_g, lon_g, laps, cur_ms, last_ms, odo, norm, x, z, valid):
        self.packet += 1
        p = self.phys
        struct.pack_into("<iff", p, 0, self.packet, thr, brk)
        struct.pack_into("<iiff", p, 16, gear + 1, int(rpm), steer, kmh)
        struct.pack_into("<fff", p, 44, lat_g, 0.0, lon_g)
        struct.pack_into("<f", p, 364, 1.0)  # embrague soltado (ACC da 1 = soltado)
        g = self.gfx
        struct.pack_into("<ii", g, 0, self.packet, 2)  # status 2 = en vivo
        struct.pack_into("<i", g, 132, laps)
        struct.pack_into("<iii", g, 140, cur_ms, last_ms if last_ms > 0 else 2147483647, 0)
        struct.pack_into("<f", g, 156, odo)
        struct.pack_into("<i", g, 160, 0)
        struct.pack_into("<f", g, 248, norm)
        struct.pack_into("<i", g, 252, 1)  # activeCars
        struct.pack_into("<fff", g, 256, x, 0.0, z)
        struct.pack_into("<i", g, 976, 7)  # carID[0]
        struct.pack_into("<i", g, 1216, 7)  # playerCarID
        struct.pack_into("<i", g, 1236, 0)  # isInPitLane
        struct.pack_into("<i", g, 1280, 3)  # ABS
        struct.pack_into("<i", g, 1408, 1 if valid else 0)

    def close(self):
        struct.pack_into("<i", self.gfx, 4, 0)
        for m in (self.phys, self.gfx, self.sta):
            m.close()


def run(args):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    port = args.puerto or {"forza": 5300, "dirt": 20777, "wrc": 20790, "acc": 0, "beamng": 4444}[args.juego]
    circuit = args.juego in ("forza", "acc", "beamng")
    length, corners = corners_for("circuito" if circuit else "tramo")
    styles = [
        dict(early_brake=0, slower_apex=0, coast=0, brake_force=0.95, brake_ramp=0.12, trail=True),
        dict(early_brake=25, slower_apex=6, coast=60, brake_force=0.7, brake_ramp=0.45, trail=False),
        dict(early_brake=8, slower_apex=2, coast=0, brake_force=0.9, brake_ramp=0.15, trail=True),
    ]
    for st in styles:
        for k in ("early_brake", "slower_apex", "coast"):
            st[k] *= 1 - args.nivel
        st["brake_force"] += (0.95 - st["brake_force"]) * args.nivel
        st["brake_ramp"] -= (st["brake_ramp"] - 0.12) * args.nivel
    laps = args.vueltas if circuit else 1

    acc = AccMemory() if args.juego == "acc" else None
    t = 0.0
    d_total = -30.0 if circuit else 0.0  # en circuito salimos un poco antes de la línea
    v = 30 / 3.6
    lap = 0
    lap_start = 0.0
    last_lap = 0.0
    best = 0.0
    driver = Driver(length, corners, circuit, styles[0])
    extra_after_finish = 3.0
    # ACC: el cronómetro se reinicia 0,4 s después de cruzar la línea y el contador de vueltas 1 s después.
    acc_timer_reset_at = None
    acc_laps_reported = 0
    acc_lap_start_shown = 0.0

    destino = "memoria compartida de ACC" if acc else f"127.0.0.1:{port}"
    print(f"Enviando telemetría de {args.juego} a {destino}…")
    try:
        while True:
            lap_d = d_total - lap * length if circuit else d_total
            thr, brk, steer = driver.controls(max(0.0, lap_d), v)
            a = thr * 7.5 * max(0.0, 1 - v / 85) - brk * 13.0 - 0.0004 * v * v
            v = max(0.0, v + a * DT)
            d_total += v * DT
            t += DT
            kmh = v * 3.6
            g, rpm = gear_rpm(kmh)
            lat_g = steer * min(2.0, v * v / 400)
            lon_g = a / 9.81

            if circuit and d_total - lap * length >= length:
                last_lap = t - lap_start
                best = last_lap if best == 0 else min(best, last_lap)
                lap += 1
                lap_start = t
                acc_timer_reset_at = t + 0.4
                print(f"  vuelta {lap} completada: {last_lap:.3f} s")
                if lap >= laps:
                    # Seguimos un poco tras la meta, como en el juego real.
                    extra_after_finish = 2.0
                driver.style = styles[lap % len(styles)]
            if circuit and lap >= laps:
                extra_after_finish -= DT
                if extra_after_finish <= 0:
                    break
            if not circuit and d_total >= length:
                extra_after_finish -= DT
                thr, brk = 0.0, 0.6
                if extra_after_finish <= 0:
                    print(f"  tramo terminado: {t:.3f} s")
                    break

            lap_d = d_total - lap * length if circuit else d_total
            x, z = position(max(0.0, lap_d), length, lateral=driver.style["slower_apex"] * 0.8)
            if args.juego == "forza":
                p = bytearray(331)
                struct.pack_into("<iI", p, 0, 1, int(t * 1000))
                struct.pack_into("<fff", p, 8, 8000.0, 900.0, rpm)
                struct.pack_into("<fff", p, 20, lat_g * 9.81, 0.0, lon_g * 9.81)
                struct.pack_into("<i", p, 212, 1234)
                dash = 232
                struct.pack_into("<fff", p, dash, x, 0.0, z)
                struct.pack_into("<f", p, dash + 12, v)
                struct.pack_into("<fffff", p, dash + 48, d_total, best, last_lap, t - lap_start, t)
                struct.pack_into("<HBBBBBBb", p, dash + 68, lap, 1, int(thr * 255), int(brk * 255), 0, 0, g, int(steer * 127))
                struct.pack_into("<i", p, 327, 42)
                sock.sendto(bytes(p), ("127.0.0.1", port))
            elif args.juego == "acc":
                if acc_timer_reset_at is not None and t >= acc_timer_reset_at:
                    acc_lap_start_shown = acc_timer_reset_at
                    acc_timer_reset_at = None
                if acc_laps_reported < lap and t >= lap_start + 1.0:
                    acc_laps_reported = lap
                cur_ms = int((t - acc_lap_start_shown) * 1000)
                norm = (max(0.0, lap_d) % length) / length
                acc.write(thr, brk, steer, g, rpm, kmh, lat_g, lon_g, acc_laps_reported, cur_ms,
                          int(last_lap * 1000), max(0.0, d_total + 30), norm, x, z, valid=True)
            elif args.juego == "dirt":
                f = [0.0] * 66
                f[0], f[1], f[2], f[3] = t, t, lap_d, d_total
                f[4], f[6] = x, z
                f[7] = v
                f[29], f[30], f[31], f[32], f[33] = thr, steer, brk, 0.0, float(g)
                f[34], f[35], f[36], f[37] = lat_g, lon_g, 0.0, rpm / 10
                f[61], f[62], f[63] = length, 0.0, 800.0
                sock.sendto(struct.pack("<66f", *f), ("127.0.0.1", port))
            elif args.juego == "beamng":
                # OutGauge (96 bytes): pedales, marcha (0 = R, 1 = N, 2 = 1ª), velocidad en m/s y RPM.
                og = struct.pack("<I4sHBBfffffffIIfff16s16si", 0, b"beam", 0, g + 1, 0, v, rpm, 0.0, 90.0, 0.5, 0.0, 90.0,
                                 0, 0, thr, brk, 0.0, b"", b"", 0)
                sock.sendto(og, ("127.0.0.1", port))
                # MotionSim («BNG1», 88 bytes): posición y velocidad en el mundo (z hacia arriba), aceleración.
                if not hasattr(run, "prev"):
                    run.prev = (x, z)
                vx, vy = (x - run.prev[0]) / DT, (z - run.prev[1]) / DT
                run.prev = (x, z)
                ms = struct.pack("<4s21f", b"BNG1", x, z, 0.0, vx, vy, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, *([0.0] * 9))
                sock.sendto(ms, ("127.0.0.1", port))
            else:  # wrc (estructura simtelemetry v3: con posición y nombres)
                pkt = struct.pack(
                    "<BB17ffddffHHHH",
                    g, 10, rpm, 8000.0, v, thr, brk, 0.0, steer, 0.0,
                    0.0, 0.0, lon_g * 9.81,      # aceleración (mundo)
                    0.0, 0.0, 1.0,               # dirección "adelante"
                    -1.0, 0.0, 0.0,              # dirección "izquierda"
                    t, lap_d, length,
                    -x, z,                       # en WRC x es positivo hacia la izquierda
                    5, 24, 4, 21,                # Rally Mediterraneo · Asco · Citroën C3 Rally2 (ids.json)
                )
                sock.sendto(pkt, ("127.0.0.1", port))

            time.sleep(DT / args.rapidez)
    finally:
        if acc:
            time.sleep(0.5)
            acc.close()

    print("Listo.")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--juego", choices=["forza", "acc", "dirt", "wrc", "beamng"], default="forza")
    ap.add_argument("--vueltas", type=int, default=3)
    ap.add_argument("--rapidez", type=float, default=1.0)
    ap.add_argument("--puerto", type=int, default=0)
    ap.add_argument("--nivel", type=float, default=0.0)
    run(ap.parse_args())
