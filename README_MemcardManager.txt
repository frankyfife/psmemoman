MEMORY CARD MANAGER – PS1 / PS2
===============================

Start: MemcardManager.exe (eine Datei, keine Installation, kein .NET nötig)

KARTEN (bis zu 4 gleichzeitig, jede in einem eigenen Segment)
  PS2  PCSX2 .ps2 (8/16/32/64 MB, mit ECC)        – lesen, schreiben, neu anlegen
       PCSX2-Ordner-Karten (_pcsx2_superblock)    – lesen, schreiben
       PS3 virtuelle Karte .VM2                    – lesen, schreiben, neu anlegen
       OPL .vmc / MemCard PRO2, SD2PSX .mc2 / .bin (ohne ECC) – lesen, schreiben, neu anlegen
  PS1  .mcr .mcd .mc .srm .ps .ddf .mci .psm (Rohformat, ePSXe/DuckStation/RetroArch/PCSX2)
       PS3 virtuelle Karte .VM1, DexDrive .gme, VGS/Bleem .vgs/.mem, POPStarter .VMC
                                                   – lesen, schreiben, neu anlegen
       PSP/Vita .VMP (SCEVMC0.VMP)                 – lesen, schreiben, neu anlegen (Signatur wird neu berechnet)
         PSP:  ms0:/PSP/SAVEDATA/<TITEL-ID>/SCEVMC0.VMP   Vita: ux0:/pspemu/PSP/SAVEDATA/<TITEL-ID>/
  Die Erkennung erfolgt am Inhalt, nicht an der Dateiendung. Alle Regionen (PAL, NTSC-U, NTSC-J,
  Korea, Asien), Shift-JIS-Titel werden korrekt angezeigt.

SAVEGAME-FORMATE
  Import PS2: PSU, MAX (Action Replay), CBS (CodeBreaker), SPS/XPS (SharkPort/X-Port),
              PSV (PS3), ZIP, Save-Ordner (mit icon.sys), komplette Karten
  Import PS1: MCS, PSX/MCB/MCX/PDA (Action Replay/GameShark/Xploder), RAW, PSV (PS3), komplette Karten
  Export PS2: PSU, MAX, CBS, Ordner, ZIP, PSV     Export PS1: MCS, PSX (AR), RAW, PSV
  PSV (PS3) wird signiert erzeugt, Dateiname wie von der PS3 (z. B. BESLES-537036B6B.PSV) → USB: PS3/EXPORT/PSV/
  Rechtsklick > "Umbenennen / Region ändern": Produktcode/Region eines Saves ändern (Original wird standardmäßig behalten).

PRÜFEN, REPARIEREN, WIEDERHERSTELLEN
  • Jede Karte zeigt oben rechts ihren Zustand: ✔ OK / ⚠ Warnungen / ✖ Fehler – Klick öffnet die Kartenprüfung.
  • Beschädigte Saves werden rot markiert und so weit wie möglich gelesen; per Export lassen sie sich retten.
  • "Reparieren" behebt nur sichere Dinge: PS2-ECC-Bitfehler, verlorene Cluster (nur wenn kein Save beschädigt ist),
    PS1-Prüfsummen, Kartenkopf, verwaiste/ungültige Blöcke. Daten, die zu beschädigten Saves gehören könnten, bleiben unangetastet.
  • PS1: gelöschte Saves, deren Blöcke noch nicht überschrieben sind, können wiederhergestellt werden.
  • "Backup zurückspielen": jede früher von diesem Programm gespeicherte Version der Karte (Ordner "Backups").
  • Unformatierte Kartendateien werden erkannt und auf Wunsch formatiert (Original wird gesichert).

DRAG & DROP
  • Save auf eine andere Karte ziehen        = kopieren   (mit Umschalt = verschieben)
  • Save in den Explorer / auf den Desktop    = exportieren (Format wie zuletzt beim Export gewählt)
  • Dateien/Ordner aus dem Explorer auf Karte = importieren
  • Kartendatei ins Fenster ziehen            = Karte öffnen
  • Kartendatei auf ein Segment ziehen        = Auswahl: öffnen oder alle Saves übernehmen

SICHERHEIT (nicht-destruktiv)
  • Änderungen passieren zuerst nur im Speicher (gelber Balken). Erst "Speichern" schreibt.
  • Beim Speichern: neues Abbild wird geprüft → Temp-Datei → zurückgelesen & verglichen →
    alte Datei nach "Backups\" kopiert → erst dann ersetzt. Schlägt etwas fehl, bleibt das Original unverändert.
  • Pro Karte werden die letzten 30 Backups im Ordner "Backups" (neben der EXE) aufbewahrt.
  • Exporte überschreiben nie vorhandene Dateien (es wird "(2)", "(3)" … angehängt).
  • Ordner-Karten: gelöschte Saves werden in den Backup-Ordner verschoben, nicht gelöscht.
  • PS1: gelöschte Saves werden wie von der Konsole nur als gelöscht markiert.
  • Wurde eine Kartendatei seit dem Laden extern verändert (z. B. von PCSX2), fragt das Programm nach.
  • Tipp: Karte vorher in PCSX2 auswerfen oder PCSX2 beenden.

OPTIONEN
  • Auto-Speichern (jede Änderung sofort mit Backup schreiben) – standardmäßig aus
  • Icons animieren, Karten beim Start wieder öffnen

TASTEN
  Strg+N neue Karte · Strg+O öffnen · Strg+S alle speichern · Entf löschen · Strg+A alle · Enter Eigenschaften

Hinweis: PSV-Dateien (PS3) können importiert, aber nicht erzeugt werden (Signatur der PS3).
Formatwissen u. a. nach mymc (Ross Ridge, Public Domain) / dart_mymc; LZARI nach H. Okumura.
