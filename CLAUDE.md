# MineServer Manager — note per Claude

Programma Windows (C# WinForms, .NET Framework 4.8) che trasforma un PC poco potente in un server Minecraft.
Utente: italiano, non sviluppatore. Mini PC Intel N95, 16 GB RAM, SSD SATA, Windows 11 LTSC + Atlas OS.
Rispondi in italiano, semplice e conciso.

## Vincoli tecnici (importanti)
- Si compila con il `csc.exe` di .NET Framework (`COMPILA.bat`): **solo C# 5**. Niente `$"..."`, `?.`, `nameof`,
  `out var`, membri `=>`, inizializzatori di auto-property, `catch ... when`.
- Sorgenti in UTF-8 compilati con `/codepage:65001`. Fine riga CRLF (`.gitattributes`).
- Nessuna dipendenza esterna: JSON con `JavaScriptSerializer` (helper `J` in `Net.cs`), zip con `System.IO.Compression`.
- Il programma salva tutto accanto all'exe (`AppConfig.BaseDir`): config.ini, server-*/, runtime/ (Java), backup/, playit/.
- Installato in `C:\MineServerManager` (cartella con permessi users-modify, così l'aggiornamento non chiede UAC).

## Struttura (src/)
- `MainForm.cs` interfaccia, 8 schede: Installa, Console, Impostazioni, Mod/Plugin, Backup, Sistema, Prestazioni, Amici.
- `Installer.cs` versioni e installazione Vanilla/Paper(Fill API v3)/Fabric/Forge, Java Temurin automatico, Modrinth, Geyser.
- `ServerProcess.cs` avvio/arresto, flag di Aikar, riavvio dopo crash, evento `Started` (pid).
- `Backup.cs` backup coerenti (save-off/save-all flush/save-on), ripristino; `Props` legge server.properties in condivisione
  (Paper lo tiene aperto).
- `PriorityManager.cs` priorità server (fino a Tempo reale), modalità server dedicato (CPU/IO/memoria di tutti gli altri
  processi abbassate e poi ripristinate), privilegi admin.
- `SystemTweaks.cs` powercfg/firewall/avvio automatico (chiave Run o attività pianificata se admin).
- `PlayitAgent.cs` playit.gg: scarica `playitd-windows-x86_64.exe`, claim con un clic nel browser (api.playit.gg
  `/claim/setup` + `/claim/exchange`, agent_type self-managed), crea il tunnel `minecraft-java` (e bedrock) con
  `/tunnels/create`, avvia `playitd --secret ... --socket-path \\.\pipe\mineserver-playitd`.
  La creazione del tunnel dopo l'approvazione NON è testabile in CI (serve un clic umano).
- `Updater.cs` aggiornamenti da GitHub Releases (verifica SHA-256 da `digest`, scambio dell'exe, fallback all'installer,
  fallback senza API se 403/429).
- `SelfTest.cs` `--selftest out.txt` (rapidi), `--selftest-e2e Tipo out.txt [versione|linea]` (prova completa),
  `--screenshots dir` (in `MainForm.ScreenshotTour`).
- `Version.cs` numero di versione: lo sostituisce il workflow di rilascio, non toccarlo a mano.

## Workflow GitHub Actions
- `build.yml`: a ogni push compila, test rapidi, installer di prova, prova completa dei 4 tipi (ultima versione).
- `compatibilita.yml`: ogni versione Fabric/Forge 1.18–1.21.x (manuale + ogni lunedì).
- `release.yml`: manuale con input `versione` e `novita` → compila, testa, Inno Setup, pubblica la release.
- `screenshots.yml`: salva gli screenshot in `docs/anteprima/`.
Per avviare un workflow da API: `gh api -X POST repos/P4ty123/Launcher-server-Mc/actions/workflows/<file>/dispatches -f ref=main`.
Dopo ogni modifica: aspetta che `build.yml` sia verde prima di pubblicare una release.

## Stato
- Release pubblicate: 1.0.0, 1.1.0 (scheda Amici con playit.gg).
- Da verificare dal vivo con l'utente: aggiornamento automatico 1.0.0 → 1.1.0, primo collegamento playit.gg.
