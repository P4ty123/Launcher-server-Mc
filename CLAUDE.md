# MineServer Manager — note per Claude

Programma Windows (C# WinForms, .NET Framework 4.8) che trasforma un PC poco potente in un server Minecraft.
Utente: italiano, non sviluppatore. Mini PC Intel N95, 16 GB RAM, SSD SATA, Windows 11 con ReviOS (non più LTSC + Atlas OS).
Rispondi in italiano, semplice e conciso.

## Vincoli tecnici (importanti)
- Si compila con il `csc.exe` di .NET Framework (`COMPILA.bat`): **solo C# 5**. Niente `$"..."`, `?.`, `nameof`,
  `out var`, membri `=>`, inizializzatori di auto-property, `catch ... when`.
- Sorgenti in UTF-8 compilati con `/codepage:65001`. Fine riga CRLF (`.gitattributes`).
- Nessuna dipendenza esterna: JSON con `JavaScriptSerializer` (helper `J` in `Net.cs`), zip con `System.IO.Compression`.
- Il programma salva tutto accanto all'exe (`AppConfig.BaseDir`): config.ini (impostazioni globali, `GlobalConfig`),
  servers/<id>.ini (un file per server, `AppConfig`), server-*/, runtime/ (Java), backup/ (server 1) e backup-<id>/, playit/.
- Installato in `C:\MineServerManager` (cartella con permessi users-modify, così l'aggiornamento non chiede UAC).

## Struttura (src/)
- `MainForm.cs` interfaccia: barra "Server:" in alto + 10 schede: 0 I miei server, 1 Installa, 2 Console, 3 Impostazioni,
  4 Mod/Plugin, 5 Backup, 6 Sistema, 7 Prestazioni, 8 Amici, 9 Personalizza (indice scheda = numero nel titolo).
  Più server (1.4): `slots` (uno `ServerSlot` per server) e `cur`; `cfg`/`server` sono proprietà = server scelto.
  Codice che lavora su un server preciso usa lo slot (StartSlot/StopSlot/CheckIdle(s)/Schedule(s)/RunBackup(s)...).
  `loadingServer` blocca i salvataggi mentre `LoadCurrentServer()` riempie le schede col server scelto.
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
  Prima di `/tunnels/create` l'agent va ACCESO: playit.gg conosce la versione dell'agent solo dopo il primo collegamento,
  altrimenti risponde `AgentVersionTooOld` (successo all'utente con la 1.2.0). Claim con version "playit x.y.z".
- `IdleSleep.cs` pausa automatica: `McProto.QueryPlayers` (Server List Ping su 127.0.0.1) conta i giocatori ogni minuto;
  vuoto per N minuti → `server.Stop()` e `WakeListener` ascolta su porta Java (stato "in pausa", al login risponde Disconnect
  e sveglia) e su UDP 19132 se Geyser (Pong RakNet, sveglia su Open Connection Request 1). Con white-list solo chi è in
  whitelist.json sveglia. Logica in MainForm: `CheckIdle`/`GoToSleep`/`WakeUp`, flag `sleeping`. Niente pausa se Chunky ha
  scritto nel log negli ultimi 5 minuti.
- `PowerPlan.cs` powercfg: acceso → Prestazioni eccellenti (duplicatescheme e9a42b02..., fallback Prestazioni elevate),
  spento/pausa → Risparmio di energia (ricreato se il sistema modificato l'ha tolto, fallback Bilanciato). GUID salvati in config.ini,
  sospensione/ibernazione a 0 su entrambi, piano originale rimesso alla chiusura (`PowerOriginalGuid`).
  Pausa = Risparmio di energia se `SaverWhenPaused` (opzione nella scheda 7, predefinita sì). `CheckPowerPlan` ogni 2 min
  rimette il piano giusto se qualcuno lo ha cambiato. Niente istruzioni sul router: l'utente usa playit.gg.
- `Customize.cs` scheda Personalizza: `McText` (codici "&" → "§" o componenti JSON per tellraw, accettati anche dalle
  26.x), motd in server.properties con escape \uXXXX e \n, logo `server-icon.png` 64x64, anteprime `ServerListPreview`/
  `ChatPreview`. Benvenuto/uscita: MainForm intercetta "<nome> joined/left the game" nel log e manda `tellraw`.
  Messaggi in config.ini (WelcomeMsg, LeaveMsg, StartMsg, PauseMotd, WakeMsg); i testi della pausa vanno a `WakeListener`.
- `Config.cs` `GlobalConfig` (config.ini + elenco server) e `AppConfig` (profilo). Migrazione: se servers/ è vuota, il vecchio
  config.ini diventa servers/1.ini (`FromLegacy`, AutoStart = StartServerOnLaunch). Id 0 = profilo di prova non salvato.
- `Servers.cs` `ServerSlot` (processo, WakeListener, pausa, cronologia console) e `ServerTools`: porte (25565+, Bedrock
  19132+ stesso scarto), conflitti di porta, porta di Geyser nel suo config.yml all'avvio, `Detect` dei server esistenti,
  RAM del PC. playit: un tunnel per porta locale, indirizzi in playit/indirizzo.txt (25565) e indirizzo-<porta>.txt.
  Updater: il flag di riavvio contiene gli id ("ids:1,3"); un flag delle versioni vecchie vale "*" (primo server).
- `Theme.cs` modalità notte (`GlobalConfig.ThemeMode` auto/light/dark, pulsante "Notte"/"Giorno" nella barra e scheda 6): colora
  ricorsivamente i controlli salvando i colori originali (si torna al chiaro senza riavvio), barra del titolo scura (DWM),
  scrollbar DarkMode_Explorer, `ThemedTabControl` disegna le linguette, intestazioni ListView disegnate a mano.
  `Tag = "keep"` = non toccare (console, anteprime, quadratini colore). `Theme.Readable` schiarisce i colori scuri.
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
- Release pubblicate: 1.0.0, 1.1.0 (scheda Amici con playit.gg), 1.2.0 (pausa automatica + piano energetico automatico), 1.2.1 (fix playit AgentVersionTooOld), 1.3.0 (scheda Personalizza), 1.3.1 (ritocchi Personalizza), 1.4.0 (più server), 1.4.1 (modalità notte, risparmio energia in pausa, via istruzioni router), 1.4.2 (Ferma pre-generazione = chunky cancel + confirm).
- Da verificare dal vivo con l'utente: aggiornamento automatico 1.0.0 → 1.1.0, primo collegamento playit.gg.
- Da verificare dal vivo: pausa automatica + risveglio (anche tramite playit.gg) e cambio piano energetico su ReviOS.
- Da verificare dal vivo (1.4.0): migrazione del server dell'utente a servers/1.ini, secondo server acceso insieme, secondo tunnel playit.
- Da verificare dal vivo (1.4.1): barra del titolo scura su Windows 11 (negli screenshot CI resta chiara), Risparmio di energia in pausa.
