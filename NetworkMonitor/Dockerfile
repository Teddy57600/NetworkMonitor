# Consultez https://aka.ms/customizecontainer pour savoir comment personnaliser votre conteneur de débogage et comment Visual Studio utilise ce Dockerfile pour générer vos images afin d’accélérer le débogage.

# Ces ARG permettent d’échanger la base utilisée pour rendre l’image finale lors du débogage à partir de VS
ARG LAUNCHING_FROM_VS
# Cette opération définit l’image de base pour la valeur finale, mais uniquement si LAUNCHING_FROM_VS a été défini
ARG FINAL_BASE_IMAGE=${LAUNCHING_FROM_VS:+aotdebug}

# Cet index est utilisé lors de l’exécution à partir de VS en mode rapide (par défaut pour la configuration de débogage)
FROM mcr.microsoft.com/dotnet/aspnet:11.0-preview AS base
WORKDIR /app


# Cette phase est utilisée pour générer le projet de service
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
# Installer les dépendances clang/zlib1g-dev pour la publication en mode natif
RUN rm -rf /var/lib/apt/lists/* \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
    clang zlib1g-dev
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["NetworkMonitor/NetworkMonitor.csproj", "NetworkMonitor/"]
RUN dotnet restore "./NetworkMonitor/NetworkMonitor.csproj"
COPY . .
WORKDIR "/src/NetworkMonitor"
RUN dotnet build "./NetworkMonitor.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Cette étape permet de publier le projet de service à copier dans la phase finale
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NetworkMonitor.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=true

# Cette phase est utilisée comme base de la phase final lors du lancement à partir de VS pour prendre en charge le débogage en mode normal (par défaut quand la configuration de débogage n’est pas utilisée)
FROM base AS aotdebug
USER root
# Installer GDB pour prendre en charge le débogage natif
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    gdb
USER app

# Cette phase est utilisée en production ou lors de l’exécution à partir de VS en mode normal (par défaut quand la configuration de débogage n’est pas utilisée)
FROM ${FINAL_BASE_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:11.0-preview} AS final
WORKDIR /app
COPY --from=publish /app/publish .

RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=Europe/Paris
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PUSHOVER_TOKEN=""
ENV PUSHOVER_USER=""
ENV PUSHOVER_STARTUP_SOUND="cosmic"
ENV PUSHOVER_SHUTDOWN_SOUND="falling"
ENV PING_TARGETS=""
ENV SCHEDULE_INTERVAL_SECONDS =""
ENV SCHEDULE_CRON = "*/3 * * * *"   # Toutes les 3 minutes par défaut
ENV DATA_DIR=/data

VOLUME /data

EXPOSE 8080

ENTRYPOINT ["./NetworkMonitor"]

ENV APP_VERSION="2.0.12"
LABEL Version="2.0.12"
