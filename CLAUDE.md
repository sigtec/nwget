# CLAUDE.md

Hinweise für Claude (und andere) zur Arbeit an diesem Repository.

## Projekt

`nwget` ist ein Kommandozeilen-Downloader in C# / .NET 10 für instabile Internetverbindungen:
blockweiser Download über parallele Verbindungen, automatische Wiederholung einzelner Blöcke,
Fortsetzen nach Abbruch. Benutzerdoku und Dateiformat: siehe `README.md`.

## Befehle

```
dotnet build                      # Build
dotnet run -- <url> [-o <pfad>] [-n <worker>] [-s <größe>] [-f]
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # bzw. linux-x64, osx-arm64, ...
```

Bauen/Installieren je Betriebssystem: siehe `README.md`, Abschnitt „Building and installing“.

Es gibt noch keine automatisierten Tests. Manueller Test: großen Download starten, mit Ctrl+C
abbrechen, erneut aufrufen; Verbindungsabbrüche durch kurzes Trennen des Netzwerks simulieren.

## Aufbau

| Datei | Inhalt |
|---|---|
| `Program.cs` | Argumente parsen, Exit-Codes (bewusst kein Ctrl+C-Handling – Konsistenz hängt nur an der Commit-Reihenfolge) |
| `Downloader.cs` | Probe, Blockdownload mit Workern, Resume-Prüfung, Fallback ohne Range-Support |
| `StateFile.cs` | Binäre `.state`-Datei (Header + Bitmap), Setzen einzelner Bits mit fsync |
| `Progress.cs` | Fortschrittszeile und Log-Ausgaben auf der Konsole (threadsicher) |

## Invarianten – bei Änderungen unbedingt erhalten

- **Die `.state` nennt nie einen Block als fertig, dessen Daten nicht auf der Platte sind.**
  Reihenfolge pro Block: Daten in `.part` schreiben → `.part` flushen (fsync) → Bit in der
  Bitmap setzen → genau dieses Byte schreiben → `.state` flushen. Flush und Bit-Setzen laufen
  unter einem gemeinsamen Lock (`_commitLock`).
- **Anlegen:** erst `.part` in Endgröße (`SetLength` + Flush), dann `.state` über Temp-Datei
  und `File.Move`.
- **Nicht fortsetzbare Reste werden nie stillschweigend verworfen:** Quelle geändert (Größe,
  ETag, Last-Modified, If-Range), `.state` kaputt, nur eine der beiden Dateien vorhanden oder
  Fortsetzen nicht möglich → Abbruch mit Exit-Code 1, Dateien bleiben unangetastet. Nur mit
  `-f` wird gelöscht und neu begonnen.
- **Dateigröße steht nicht in der `.state`**, sie ergibt sich aus der Länge der `.part`.
- **Blockgröße:** `-s` gilt nur für neue Downloads; beim Fortsetzen gilt die aus der `.state`.
- Alles andere (Queue, Retry-Zähler, Backoff, Fortschritt) lebt nur im Speicher.
- Blöcke werden direkt an ihren Offset in die `.part` gestreamt (kein Puffer in Blockgröße).
  Empfangene Daten werden auch bei Abbruch noch geschrieben, ein vollständiger Block noch
  committet (Schreiben ohne CancellationToken). Halb geschriebene Blöcke ohne Bit sind harmlos.
- Schreibfehler auf der Platte sind nie transient (`DownloadException`), sonst landen sie in der
  Retry-Schleife.
- Änderungen am `.state`-Format erfordern eine neue Formatversion in `StateFile`.

## Konventionen

- Programmausgaben, Fehlermeldungen, Kommentare und `README.md` auf Englisch.
- Keine Lokalisierung: Meldungen von Betriebssystem/.NET (Exceptions) werden unverändert
  ausgegeben, auch wenn sie lokalisiert sind.
- Keine externen NuGet-Pakete; keine Reflection – das Tool soll trimming- und Native-AOT-fähig bleiben.
- Nullable Reference Types sind aktiv, Warnungen sauber halten.
- Einfachheit vor Features: Das Tool soll bewusst klein und gut nachvollziehbar bleiben.
