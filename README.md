# FamilyCalendar

Calendario familiare installabile (PWA). Frontend HTML/CSS/JS, backend C# (.NET 8), database MySQL.
Login come ComitatoFeste: utente `iniziale.cognome` + password condivisa.

## Avvio locale (Docker)

```
docker compose up --build     # app su http://localhost:8080, MySQL su localhost:3307
docker compose down -v        # spegne e cancella i dati
```

Login di prova: `g.lima` (admin) o `m.rossi`, password `famiglia` (admin: `admin`).
Variabili sovrascrivibili da `.env` (vedi `docker-compose.yml`).

## Configurazione (env)

| Variabile | Significato |
|---|---|
| `FAMILYCALENDAR_CONNECTION` | Stringa di connessione MySQL (Aiven: aggiungere `SslMode=Required`) |
| `FAMILYCALENDAR_AUTH_PASSWORD` | Password condivisa (vuota = login disattivato) |
| `FAMILYCALENDAR_AUTH_PASSWORD_ADMIN` | Password admin (solo per membri admin) |
| `FAMILYCALENDAR_AUTH_SECRET` | Segreto per firmare i token (fisso in produzione) |
| `FAMILYCALENDAR_MEMBERS` | Membri iniziali `Nome Cognome[:admin];...`, creati se la tabella è vuota |
| `PORT` | Porta di ascolto (Render) |

Le migration EF si applicano da sole all'avvio. Nuova migration: `dotnet ef migrations add <Nome> -o Data/Migrations` da `Src/FamilyCalendar.Api`.
