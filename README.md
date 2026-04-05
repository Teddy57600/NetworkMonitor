# NetworkMonitor

<p align="center">
  <img alt=".NET 11" src="https://img.shields.io/badge/.NET-11-512BD4?style=for-the-badge&logo=dotnet" />
  <img alt="Docker" src="https://img.shields.io/badge/Docker-ready-2496ED?style=for-the-badge&logo=docker&logoColor=white" />
  <img alt="AOT" src="https://img.shields.io/badge/Native%20AOT-enabled-0A0A0A?style=for-the-badge" />
  <img alt="Pushover" src="https://img.shields.io/badge/Notifications-Pushover-F36C3D?style=for-the-badge" />
</p>

<p align="center">
  Supervision réseau légère en .NET, avec surveillance ICMP/TCP, notifications Pushover,
  persistance d'état, exécution Docker et configuration rechargeable à chaud.
</p>

---

## ✨ Vue d'ensemble

**NetworkMonitor** est une application console .NET pensée pour superviser simplement des IP et des services TCP, sans stack de monitoring lourde.

Elle permet de :

- surveiller des **adresses IP** via `ping`
- surveiller des **services TCP** via `host:port`
- afficher un **tableau de bord web responsive** avec vue temps réel
- envoyer des notifications **Pushover** lors d'une panne, d'une reprise et d'une indisponibilité prolongée
- envoyer une notification **au démarrage et à l'arrêt** du service
- persister l'état des moniteurs et des snoozes dans `state.json`
- écrire des logs dans la console et sur disque
- piloter l'exécution via **intervalle** ou **CRON**
- configurer l'application par **variables d'environnement** ou par **fichier YAML**
- recharger la configuration YAML **sans redémarrage**
- tourner localement ou dans **Docker**

Le projet cible **.NET 11** et active la **publication Native AOT**.

---

## 🚀 Fonctionnalités

### Surveillance ICMP
- lecture des cibles via `PING_TARGETS` ou YAML
- jusqu'à **3 tentatives de ping** par cycle
- passage en état **DOWN** après **3 cycles échoués consécutifs**
- notification `🟢 RECOVERY` dès qu'une cible redevient joignable

### Surveillance TCP
- lecture des cibles via `TCP_TARGETS` ou YAML
- vérification de disponibilité sur des couples `host:port`
- jusqu'à **3 tentatives de connexion** par cycle
- notification de reprise quand le service redevient accessible

### Alertes Pushover
- notification `🔴 DOWN`
- notification `🚨 STILL DOWN` en priorité urgente après **5 minutes** d'incident continu
- notification `🟢 RECOVERY`
- notification `🚀` au démarrage et `🛑` à l'arrêt
- sons de démarrage et d'arrêt configurables

### Snooze intelligent
- un `receipt` Pushover est surveillé pour les alertes urgentes
- si l'alerte est acquittée, un **snooze** est activé pour le moniteur concerné
- le snooze est **par hôte** ou **par `host:port`**
- une notification `RECOVERY` est toujours envoyée, même pendant un snooze
- lors d'un `RECOVERY`, le snooze du moniteur concerné est automatiquement supprimé

### Rechargement de configuration à chaud
- si le fichier YAML change pendant l'exécution, l'application le recharge automatiquement
- les cibles ping et TCP sont ajoutées ou retirées sans redémarrage
- les paramètres Pushover, le planning et `snoozeDays` sont pris en compte dynamiquement
- l'attente entre deux cycles est interrompue si la configuration change

### Résilience
- circuit breaker temporaire après détection d'une panne
- persistance d'état dans `state.json`
- conservation du contexte entre redémarrages

### Observabilité
- logs console en temps réel
- logs journaliers dans `DATA_DIR/logs`
- tableau de bord web embarqué sur le port **8080** par défaut

### Interface web
- API JSON `GET /api/dashboard`
- endpoint de santé `GET /api/health`
- action web pour demander un **check immédiat**
- action web pour **supprimer un snooze** sur un moniteur
- formulaires web pour **ajouter une cible Ping** ou **un test TCP**
- boutons web pour **supprimer une cible Ping** ou **un test TCP**
- rafraîchissement automatique configurable

---

## 🧱 Stack technique

- **.NET 11**
- **C#**
- **Cronos** pour la planification CRON
- **Microsoft.Extensions.Logging** pour les logs
- **Pushover API** pour les notifications
- **Docker** pour l'exécution conteneurisée
- **Native AOT** pour la publication

---

## 📦 Structure du projet

```text
NetworkMonitor/
├─ NetworkMonitor/
│  ├─ Configuration/
│  │  └─ AppConfigProvider.cs
│  ├─ Dashboard/
│  │  ├─ DashboardSnapshotModels.cs
│  │  ├─ DashboardWebServer.cs
│  │  └─ ManualCheckTrigger.cs
│  ├─ Monitoring/
│  │  ├─ MonitorState.cs
│  │  └─ TcpPortMonitorState.cs
│  ├─ Notifications/
│  │  ├─ PushoverClient.cs
│  │  └─ PushoverSnooze.cs
│  ├─ Scheduling/
│  │  ├─ CronSchedule.cs
│  │  ├─ IntervalSchedule.cs
│  │  └─ ISchedule.cs
│  ├─ CronDescription.cs
│  ├─ FileLogger.cs
│  ├─ Program.cs
│  ├─ StateStore.cs
│  ├─ Dockerfile
│  ├─ NetworkMonitor.csproj
│  ├─ config.yaml.example
│  └─ wwwroot/
│     ├─ app.js
│     ├─ index.html
│     └─ site.css
└─ README.md
```

---

## ⚙️ Configuration

L'application peut être configurée de deux façons :

1. par **variables d'environnement**
2. par **fichier YAML**

Le mode actuel par variables d'environnement reste entièrement supporté.

### Priorité de résolution

Pour chaque valeur :

- si une valeur YAML est présente, elle est utilisée
- sinon, la variable d'environnement correspondante est utilisée
- sinon, la valeur par défaut s'applique

Exception pour les cibles de supervision :

- `pingTargets` du YAML **s'ajoute** à `PING_TARGETS`
- `tcpTargets` du YAML **s'ajoute** à `TCP_TARGETS`
- les doublons sont supprimés automatiquement

Exemple :

```text
PING_TARGETS=1.1.1.1
TCP_TARGETS=google.com:443
```

et dans le YAML :

```yaml
monitoring:
  pingTargets:
    - 8.8.8.8
  tcpTargets:
    - localhost:80
```

Résultat effectif :

- ping monitorés : `1.1.1.1`, `8.8.8.8`
- endpoints TCP monitorés : `google.com:443`, `localhost:80`

### Variables d'environnement disponibles

| Variable | Description | Exemple | Valeur par défaut |
|---|---|---|---|
| `PING_TARGETS` | Liste d'IPs à surveiller, séparées par des virgules | `192.168.1.1,8.8.8.8` | vide |
| `TCP_TARGETS` | Liste d'endpoints TCP `host:port`, séparés par des virgules | `google.com:443,192.168.1.10:22` | vide |
| `SCHEDULE_INTERVAL_SECONDS` | Intervalle entre deux cycles | `10` | `10` |
| `SCHEDULE_CRON` | Expression CRON | `*/3 * * * *` | non définie |
| `PUSHOVER_TOKEN` | Token d'application Pushover | `xxxxx` | vide |
| `PUSHOVER_USER` | Clé utilisateur ou groupe Pushover | `xxxxx` | vide |
| `PUSHOVER_STARTUP_SOUND` | Son de la notification de démarrage | `cosmic` | `cosmic` |
| `PUSHOVER_SHUTDOWN_SOUND` | Son de la notification d'arrêt | `falling` | `falling` |
| `SNOOZE_DAYS` | Nombre de jours de snooze après acquittement | `3` | `1` |
| `DASHBOARD_ENABLED` | Active ou désactive le serveur web du dashboard | `true` | `true` |
| `DASHBOARD_AUTH_ENABLED` | Active la page de connexion du dashboard | `true` | `false` |
| `DASHBOARD_AUTH_USERNAME` | Nom d'utilisateur du dashboard | `admin` | vide |
| `DASHBOARD_AUTH_PASSWORD` | Mot de passe du dashboard | `change-me` | vide |
| `DASHBOARD_REFRESH_SECONDS` | Intervalle de rafraîchissement automatique de l'interface web | `5` | `5` |
| `DATA_DIR` | Répertoire de persistance (`state.json`, logs, config YAML par défaut) | `/data` | `.` |
| `APP_VERSION` | Version affichée dans les logs/notifications | `1.5.0` | `inconnue` |
| `CONFIG_YAML_PATH` | Chemin explicite du fichier YAML | `/config/config.yaml` | `DATA_DIR/config.yaml` |
| `TZ` | Fuseau horaire du conteneur | `Europe/Paris` | `Europe/Paris` dans l'image Docker |
| `ASPNETCORE_URLS` | URLs d'écoute du serveur web embarqué | `http://0.0.0.0:8080` | `http://0.0.0.0:8080` |

### Priorité entre CRON et intervalle

Le comportement reste le suivant :

1. si `schedule.cron` ou `SCHEDULE_CRON` est défini, il est utilisé
2. sinon, `schedule.intervalSeconds` ou `SCHEDULE_INTERVAL_SECONDS` est utilisé
3. sinon, l'application tourne toutes les **10 secondes**

---

## 📝 Configuration YAML

### Emplacement

Par défaut, l'application cherche le fichier ici :

```text
DATA_DIR/config.yaml
```

Ce chemin peut être surchargé avec :

```text
CONFIG_YAML_PATH
```

### Rechargement à chaud

Le fichier YAML est relu automatiquement lorsqu'il change :

- au début de chaque cycle
- pendant l'attente jusqu'au prochain cycle

Il n'est pas nécessaire de redémarrer l'application.

### Détection des changements

Le rechargement à chaud repose sur un mode **hybride** :

- **`FileSystemWatcher`** pour une détection quasi immédiate des changements
- **repli périodique automatique** toutes les **30 secondes** pour rester fiable en environnement Docker/Linux

Ce choix est volontaire : dans un conteneur Linux, `FileSystemWatcher` s'appuie sur **inotify**, ce qui fonctionne généralement très bien, y compris sur des volumes montés. En revanche, selon le type de montage ou le système de fichiers hôte, certains événements peuvent être regroupés, retardés ou ne pas remonter comme attendu.

Le comportement final est donc :

- si l'événement système remonte correctement, la configuration est rechargée presque immédiatement
- sinon, le contrôle périodique garantit qu'une modification sera quand même détectée sans redémarrage

Les changements suivants sont pris en compte :

- modification du contenu du fichier
- création du fichier
- suppression du fichier
- renommage / remplacement atomique du fichier
- erreur interne du watcher, avec bascule implicite sur le repli périodique

### Recommandations Docker / production

- un montage `:ro` sur le fichier YAML fonctionne très bien si la mise à jour est faite **depuis l'hôte**
- en revanche, un fichier monté en `:ro` ne peut évidemment pas être modifié **depuis le conteneur**
- en production, le plus simple est donc de modifier `config.yaml` côté hôte, puis de laisser l'application détecter automatiquement le changement
- pour des mises à jour plus fiables, privilégier un **remplacement atomique** du fichier (écriture dans un fichier temporaire puis renommage)
- si votre environnement de stockage remonte mal les événements fichiers, le repli périodique continue malgré tout d'assurer la prise en compte des changements
- si vous souhaitez **ajouter des cibles depuis l'interface web**, le montage YAML doit être **inscriptible** (`rw`)
- la suppression depuis l'interface web retire uniquement les cibles présentes dans le **YAML** ; une cible fournie par variable d'environnement reste monitorée

#### Exemple de mise à jour atomique en PowerShell

```powershell
$configPath = ".\config.yaml"
$tempPath = ".\config.yaml.tmp"

@'
appVersion: "1.6.1"
snoozeDays: 2

schedule:
  intervalSeconds: 15

monitoring:
  pingTargets:
    - 1.1.1.1
'@ | Set-Content -Path $tempPath -Encoding UTF8

Move-Item -Path $tempPath -Destination $configPath -Force
```

#### Exemple de mise à jour atomique en Bash

```bash
CONFIG_PATH=./config.yaml
TEMP_PATH=./config.yaml.tmp

cat > "$TEMP_PATH" <<'EOF'
appVersion: "1.6.1"
snoozeDays: 2

schedule:
  intervalSeconds: 15

monitoring:
  pingTargets:
    - 1.1.1.1
EOF

mv -f "$TEMP_PATH" "$CONFIG_PATH"
```

### Exemple

Un exemple prêt à l'emploi est fourni dans :

```text
NetworkMonitor/config.yaml.example
```

Contenu type :

```yaml
appVersion: "1.6.0"
snoozeDays: 3

schedule:
  cron: "*/30 * * * * *"
  # intervalSeconds: 10

dashboard:
  enabled: true
  authEnabled: false
  authUsername: "admin"
  authPassword: "change-me"
  refreshSeconds: 5

pushover:
  token: "your-pushover-token"
  user: "your-pushover-user"
  startupSound: "cosmic"
  shutdownSound: "falling"

monitoring:
  pingTargets:
    - 1.1.1.1
    - 8.8.8.8
  tcpTargets:
    - google.com:443
    - host: localhost
      port: 80
```

### Clés YAML supportées

| Clé | Description |
|---|---|
| `appVersion` | Version affichée par l'application |
| `snoozeDays` | Durée du snooze après acquittement |
| `schedule.cron` | Planification CRON |
| `schedule.intervalSeconds` | Planification par intervalle |
| `dashboard.enabled` | Active ou désactive le serveur web du dashboard |
| `dashboard.authEnabled` | Active la page de connexion du dashboard |
| `dashboard.authUsername` | Nom d'utilisateur du dashboard |
| `dashboard.authPassword` | Mot de passe du dashboard |
| `dashboard.refreshSeconds` | Intervalle de rafraîchissement de l'interface web |
| `pushover.token` | Token Pushover |
| `pushover.user` | Utilisateur/groupe Pushover |
| `pushover.startupSound` | Son de démarrage |
| `pushover.shutdownSound` | Son d'arrêt |
| `monitoring.pingTargets` | Liste des IP surveillées |
| `monitoring.tcpTargets` | Liste des endpoints TCP |

Pour `tcpTargets`, les deux syntaxes suivantes sont acceptées :

```yaml
tcpTargets:
  - google.com:443
  - host: localhost
    port: 80
```

---

## 🔔 Comportement des alertes

### Ping

Pour une cible IP :

- un cycle effectue jusqu'à **3 tentatives de ping**
- après **3 cycles échoués consécutifs**, la cible passe en **DOWN**
- une notification `🔴 DOWN` est envoyée
- si la panne dure plus de **5 minutes**, une notification `🚨 STILL DOWN` est envoyée en priorité urgente
- dès que la cible répond à nouveau, une notification `🟢 RECOVERY` est envoyée

### TCP

Pour une cible TCP :

- un cycle effectue jusqu'à **3 tentatives de connexion**
- au premier passage en panne, une notification `🔴 DOWN` est envoyée
- si la panne dure plus de **5 minutes**, une notification `🚨 STILL DOWN` est envoyée en priorité urgente
- dès qu'une connexion réussit, une notification `🟢 RECOVERY` est envoyée

### Snooze Pushover

Lorsqu'une notification urgente est acquittée côté Pushover :

- l'application sonde le `receipt` retourné par Pushover
- si l'alerte est acquittée, un snooze est activé pour le moniteur concerné
- pendant ce snooze, les notifications `DOWN` et `STILL DOWN` suivantes sont ignorées pour ce moniteur
- un `RECOVERY` est toujours envoyé
- après `RECOVERY`, le snooze de ce moniteur est supprimé
- si le moniteur retombe ensuite, il peut à nouveau générer un nouveau snooze après acquittement

### Notifications de cycle de vie

Au démarrage :

- une notification `🚀 NetworkMonitor démarré` est envoyée
- elle contient la version, le rythme d'exécution et le nombre de cibles surveillées

À l'arrêt :

- une notification `🛑 NetworkMonitor arrêté` est envoyée
- elle contient le motif d'arrêt si disponible

Motifs d'arrêt actuellement distingués :

- arrêt normal
- arrêt manuel demandé depuis la console
- interruption terminal (`SIGINT`)
- arrêt Docker / système (`SIGTERM`)
- exception inattendue

---

## 🗂️ Persistance et fichiers générés

Le répertoire `DATA_DIR` contient :

### `state.json`

Ce fichier stocke notamment :

- l'état des moniteurs
- la date de début d'incident
- les snoozes actifs par cible

### `logs/`

Contient un fichier journalier :

```text
networkmonitor-YYYY-MM-DD.log
```

### `config.yaml`

Si utilisé, le fichier YAML peut être placé dans `DATA_DIR` pour centraliser configuration et état.

Exemple d'arborescence :

```text
/data/
├─ config.yaml
├─ state.json
└─ logs/
   └─ networkmonitor-2026-01-18.log
```

---

## 🏁 Démarrage rapide

### Prérequis

- **.NET 11 SDK**
- ou **Docker**
- un compte **Pushover** si vous souhaitez les notifications

### Exécution locale avec variables d'environnement

```powershell
$env:PING_TARGETS="8.8.8.8,1.1.1.1"
$env:TCP_TARGETS="google.com:443,localhost:22"
$env:SCHEDULE_INTERVAL_SECONDS="15"
$env:DASHBOARD_REFRESH_SECONDS="5"
$env:PUSHOVER_TOKEN="your-token"
$env:PUSHOVER_USER="your-user"
$env:DATA_DIR=".data"
dotnet run --project .\NetworkMonitor\NetworkMonitor.csproj
```

Le tableau de bord est ensuite disponible sur :

```text
http://localhost:8080
```

### Exécution locale avec YAML

```powershell
New-Item -ItemType Directory -Force .data | Out-Null
Copy-Item .\NetworkMonitor\config.yaml.example .\.data\config.yaml
$env:DATA_DIR=".data"
dotnet run --project .\NetworkMonitor\NetworkMonitor.csproj
```

---

## 🐳 Utilisation avec Docker

Le projet contient un `Dockerfile` prêt à l'emploi.

### Particularités de l'image

- installation de `tzdata`
- fuseau horaire du conteneur fixé à **`Europe/Paris`**
- volume `/data` pour la persistance
- dashboard web exposé sur le port **8080**

### Build

```bash
docker build -t networkmonitor .
```

### Run avec variables d'environnement

```bash
docker run -d \
  --name networkmonitor \
  -p 8080:8080 \
  -e PING_TARGETS="8.8.8.8,1.1.1.1" \
  -e TCP_TARGETS="google.com:443,192.168.1.10:22" \
  -e SCHEDULE_CRON="*/3 * * * *" \
  -e DASHBOARD_REFRESH_SECONDS="5" \
  -e DASHBOARD_AUTH_ENABLED="true" \
  -e DASHBOARD_AUTH_USERNAME="admin" \
  -e DASHBOARD_AUTH_PASSWORD="change-me" \
  -e PUSHOVER_TOKEN="your-token" \
  -e PUSHOVER_USER="your-user" \
  -e PUSHOVER_STARTUP_SOUND="cosmic" \
  -e PUSHOVER_SHUTDOWN_SOUND="falling" \
  -e SNOOZE_DAYS="1" \
  -e DATA_DIR="/data" \
  -v networkmonitor-data:/data \
  networkmonitor
```

### Run avec YAML monté dans `/config`

```bash
docker run -d \
  --name networkmonitor \
  -p 8080:8080 \
  -e DATA_DIR="/data" \
  -e CONFIG_YAML_PATH="/config/config.yaml" \
  -v ${PWD}/config.yaml:/config/config.yaml \
  -v networkmonitor-data:/data \
  networkmonitor
```

> Si vous voulez empêcher les modifications du YAML depuis l'interface web, remplacez ce montage par `:ro`.

### Docker Compose

Un fichier `.env.example` est fourni à la racine pour personnaliser facilement l'exemple Compose.

Exemple :

```bash
cp .env.example .env
```

```yaml
services:
  networkmonitor:
    build:
      context: .
      dockerfile: NetworkMonitor/Dockerfile
    container_name: networkmonitor
    restart: unless-stopped
    environment:
      DATA_DIR: /data
      CONFIG_YAML_PATH: /config/config.yaml
      DASHBOARD_AUTH_ENABLED: "true"
      DASHBOARD_AUTH_USERNAME: "admin"
      DASHBOARD_AUTH_PASSWORD: "change-me"
    ports:
      - 8080:8080
    volumes:
      - ./config.yaml:/config/config.yaml
      - networkmonitor-data:/data

  watchtower:
    image: containrrr/watchtower:latest
    container_name: networkmonitor-watchtower
    restart: unless-stopped
    command: --interval 3600 --cleanup --label-enable
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock

volumes:
  networkmonitor-data:
```

> Sous Docker, l'arrêt via `docker stop` envoie un `SIGTERM`. L'application intercepte ce signal pour effectuer un arrêt propre et envoyer la notification de fin.

### Auto-update en production avec Watchtower

Pour un usage production simple avec `docker-compose.yaml`, le plus fiable est d'utiliser un conteneur **Watchtower** qui surveille les nouvelles versions d'image et redémarre le service automatiquement.

Principe :

- Watchtower vérifie périodiquement si une nouvelle image est disponible dans le registre
- si une nouvelle version est trouvée, elle est téléchargée
- le conteneur applicatif est recréé puis redémarré automatiquement

Pré-requis importants :

- utiliser une **image publiée dans un registre** (`ghcr.io/...`, Docker Hub, etc.)
- éviter un déploiement basé uniquement sur `build: .` si vous voulez un vrai auto-update depuis un registre
- conserver `/data` en volume persistant

Variables d'exemple fournies dans `.env.example` :

- `NETWORKMONITOR_WATCHTOWER_CONTAINER_NAME`
- `NETWORKMONITOR_WATCHTOWER_INTERVAL_SECONDS`

> Remarque : l'auto-update entraînera généralement un **court redémarrage** du service. Ce n'est pas un mécanisme de zero-downtime.

### Tableau de bord web et API

Par défaut, l'interface web est disponible ici :

```text
http://localhost:8080
```

Endpoints utiles :

- `GET /api/dashboard` : snapshot complet du dashboard
- `GET /api/health` : endpoint de santé simple
- `POST /api/actions/check-now` : demande un cycle de vérification immédiat
- `POST /api/actions/clear-snooze?key=...` : supprime le snooze d'un moniteur
- `POST /api/actions/add-ping?target=...` : ajoute une cible Ping au YAML
- `POST /api/actions/add-tcp?host=...&port=...` : ajoute un test TCP au YAML
- `POST /api/actions/remove-ping?target=...` : supprime une cible Ping du YAML
- `POST /api/actions/remove-tcp?host=...&port=...` : supprime un test TCP du YAML

Le dashboard affiche notamment :

- les compteurs globaux UP / DOWN / snoozés
- les moniteurs Ping et TCP
- la date du dernier contrôle, du dernier succès et du dernier échec
- la durée du dernier test
- l'état du circuit breaker
- la fin éventuelle du snooze
- des formulaires pour ajouter rapidement des cibles Ping et TCP
- des boutons pour supprimer rapidement des cibles gérées par le YAML

### Sécurité du dashboard

Une authentification simple via **page de connexion + cookie de session** peut être activée via :

- `dashboard.authEnabled`, `dashboard.authUsername`, `dashboard.authPassword` dans le YAML
- ou `DASHBOARD_AUTH_ENABLED`, `DASHBOARD_AUTH_USERNAME`, `DASHBOARD_AUTH_PASSWORD` via l'environnement

Exemple YAML :

```yaml
dashboard:
  enabled: true
  authEnabled: true
  authUsername: "admin"
  authPassword: "change-me"
  refreshSeconds: 5
```

Quand elle est activée :

- l'utilisateur arrive d'abord sur une page de connexion dédiée
- après authentification, une session web est conservée via cookie
- les routes du dashboard et les API associées sont protégées

> Recommandation : si le dashboard est exposé hors du réseau local, placez-le derrière **HTTPS** ou un **reverse proxy** (Nginx, Traefik, Caddy). Même avec une page de connexion, un trafic HTTP non chiffré reste à éviter.

#### Exemple Traefik

Exemple `docker-compose` minimal avec Traefik en frontal TLS :

```yaml
services:
  traefik:
    image: traefik:v3.1
    command:
      - --providers.docker=true
      - --providers.docker.exposedbydefault=false
      - --entrypoints.web.address=:80
      - --entrypoints.websecure.address=:443
      - --certificatesresolvers.letsencrypt.acme.tlschallenge=true
      - --certificatesresolvers.letsencrypt.acme.email=admin@example.com
      - --certificatesresolvers.letsencrypt.acme.storage=/letsencrypt/acme.json
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - traefik-letsencrypt:/letsencrypt

  networkmonitor:
    build:
      context: .
      dockerfile: NetworkMonitor/Dockerfile
    restart: unless-stopped
    environment:
      DATA_DIR: /data
      CONFIG_YAML_PATH: /config/config.yaml
      DASHBOARD_AUTH_ENABLED: "true"
      DASHBOARD_AUTH_USERNAME: "admin"
      DASHBOARD_AUTH_PASSWORD: "change-me"
    volumes:
      - ./config.yaml:/config/config.yaml
      - networkmonitor-data:/data
    labels:
      - traefik.enable=true
      - traefik.http.routers.networkmonitor.rule=Host(`monitor.example.com`)
      - traefik.http.routers.networkmonitor.entrypoints=websecure
      - traefik.http.routers.networkmonitor.tls=true
      - traefik.http.routers.networkmonitor.tls.certresolver=letsencrypt
      - traefik.http.services.networkmonitor.loadbalancer.server.port=8080

volumes:
  networkmonitor-data:
  traefik-letsencrypt:
```

Dans cet exemple :

- Traefik publie le dashboard en **HTTPS** sur `monitor.example.com`
- l'application continue d'écouter en interne sur le port `8080`
- la page de connexion applicative peut rester active en complément
- il n'est plus nécessaire d'exposer directement `8080` sur l'hôte

---

## 🕒 Exemples d'expressions CRON

| Expression | Interprétation |
|---|---|
| `*/3 * * * *` | toutes les 3 minutes |
| `0 */2 * * *` | toutes les 2 heures |
| `0 8 * * 1` | chaque lundi à 08h00 |
| `0 0 1 * *` | le 1er de chaque mois à minuit |
| `*/30 * * * * *` | toutes les 30 secondes |

Les expressions sont évaluées avec le fuseau horaire local du processus. Dans l'image Docker fournie, ce fuseau est `Europe/Paris`.

---

## 🪵 Logs et exploitation

Au démarrage et pendant l'exécution, l'application journalise notamment :

- la version applicative
- la description de la planification active
- les événements `DOWN`, `STILL DOWN` et `RECOVERY`
- les rechargements de configuration YAML
- les périodes de snooze
- les erreurs d'envoi de notifications
- les erreurs de lecture/écriture de `state.json`

---

## 🔐 Notes d'exploitation

- si aucune cible ping n'est configurée, l'application démarre avec avertissement
- si aucune cible TCP n'est configurée, l'application démarre aussi avec avertissement
- sans `PUSHOVER_TOKEN` et `PUSHOVER_USER`, les notifications échoueront
- `DATA_DIR` doit être persistant en environnement conteneurisé
- `state.json` est compatible avec l'exécution AOT grâce à la sérialisation source-generated
- le parseur YAML intégré supporte le format documenté ici ; il ne vise pas un moteur YAML générique complet

---

## 🛠️ Développement

### Restaurer les dépendances

```bash
dotnet restore NetworkMonitor/NetworkMonitor.csproj
```

### Compiler

```bash
dotnet build NetworkMonitor/NetworkMonitor.csproj -c Release
```

### Publier

```bash
dotnet publish NetworkMonitor/NetworkMonitor.csproj -c Release
```

---

## 📘 Cas d'usage typiques

- supervision basique d'équipements réseau internes
- vérification de disponibilité Internet ou DNS publics
- contrôle d'ouverture de ports critiques
- surveillance légère de services auto-hébergés
- alerting simple sans stack de supervision lourde

---

## 🤝 Contribution

Les contributions sont les bienvenues pour :

- enrichir les types de sondes
- améliorer la stratégie de retry/circuit breaker
- ajouter des tests automatisés
- proposer d'autres canaux de notification
- améliorer l'expérience Docker et CI

---

## 📄 Licence

Aucune licence n'est actuellement documentée dans le dépôt.

---

## ❤️ Résumé

**NetworkMonitor** fournit un monitoring réseau simple à déployer, avec :

- un binaire léger
- de la persistance
- des alertes Pushover utiles
- un snooze intelligent par cible
- une configuration YAML rechargeable à chaud
- une exécution fiable en local ou dans Docker
