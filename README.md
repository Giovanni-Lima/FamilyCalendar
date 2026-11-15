# FamilyCalendar

Calendario familiare installabile (PWA). Frontend HTML/CSS/JS, backend C# (.NET 8), database MySQL.
Login come ComitatoFeste: utente `iniziale.cognome` + password condivisa.

## Avvio locale (Docker)

```
docker compose up --build     # app su http://localhost:8080, MySQL su localhost:3307
docker compose down -v        # spegne e cancella i dati
```

Login di prova: `g.lima` (admin) o `m.rossi`, password `famiglia` (admin: `admin` solo come default del compose; la password reale si imposta in `.env`).
Variabili sovrascrivibili da `.env` (vedi `docker-compose.yml`).

## Configurazione (env)

| Variabile | Significato |
|---|---|
| `FAMILYCALENDAR_CONNECTION` | Stringa di connessione MySQL (Aiven: aggiungere `SslMode=Required`) |
| `FAMILYCALENDAR_AUTH_PASSWORD` | Password condivisa (vuota = login disattivato) |
| `FAMILYCALENDAR_AUTH_PASSWORD_ADMIN` | Password admin (solo per membri admin) |
| `FAMILYCALENDAR_AUTH_SECRET` | Segreto per firmare i token (fisso in produzione) |
| `FAMILYCALENDAR_MEMBERS` | Membri iniziali `Nome Cognome[:admin];...`, creati se la tabella è vuota |
| `GEMINI_API_KEY` | Chiave API Gemini per l'assistente Æsir (senza, l'assistente risponde 503 "non configurato") |
| `GROQ_API_KEY` | Chiave API Groq, fallback per le risposte (opzionale) |
| `ASSISTANT_PER_USER_PER_HOUR` | Richieste all'assistente per utente all'ora (default 10, admin esenti) |
| `ASSISTANT_GLOBAL_PER_DAY` | Richieste globali all'assistente al giorno (default 80, admin esenti) |
| `ASSISTANT_MAX_CONCURRENT` | Richieste contemporanee all'assistente (default 2) |
| `PORT` | Porta di ascolto (Render) |

Le migration EF si applicano da sole all'avvio. Nuova migration: `dotnet ef migrations add <Nome> -o Data/Migrations` da `Src/FamilyCalendar.Api`.

## Assistente Æsir

Risponde a domande in linguaggio naturale sulle attività del calendario (es. "quali sono i prossimi eventi?").
Usa embedding Gemini delle attività, salvati in MySQL (tabella `TaskEmbeddings`) e confrontati con
similarità coseno in-process; le risposte sono generate da Gemini, con fallback su Groq.

- Indicizzazione automatica: a ogni creazione/modifica di un'attività, più un backfill all'avvio.
- Attivazione: imposta `GEMINI_API_KEY` (ed eventualmente `GROQ_API_KEY`) nel file `.env`, poi `docker compose up -d app`.
- Limiti per utente e globali configurabili con le variabili `ASSISTANT_*` sopra (gli admin sono esenti).
- Gli embedding sono rigenerabili in qualsiasi momento: la tabella `TaskEmbeddings` è solo una cache dell'indice.
- Privacy: il testo delle attività viene inviato a Google (Gemini) e a Groq.
