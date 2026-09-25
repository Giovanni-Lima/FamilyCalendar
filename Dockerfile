# syntax=docker/dockerfile:1
#
# Immagine dell'API (FamilyCalendar.Api), che serve anche il frontend statico da wwwroot.
# Build context = radice del repo. Usabile da docker-compose e (in futuro) da Render.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Src/FamilyCalendar.Api/FamilyCalendar.Api.csproj Src/FamilyCalendar.Api/
RUN dotnet restore Src/FamilyCalendar.Api/FamilyCalendar.Api.csproj

COPY Src/ Src/
RUN dotnet publish Src/FamilyCalendar.Api/FamilyCalendar.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_ENVIRONMENT=Production
# Render inietta PORT a runtime; Program.cs la legge. 8080 è il fallback.
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FamilyCalendar.Api.dll"]
