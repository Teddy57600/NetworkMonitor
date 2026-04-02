# NetworkMonitor

<p align="center">
  <img alt=".NET 11" src="https://img.shields.io/badge/.NET-11-512BD4?style=for-the-badge&logo=dotnet" />
  <img alt="Docker" src="https://img.shields.io/badge/Docker-ready-2496ED?style=for-the-badge&logo=docker&logoColor=white" />
  <img alt="AOT" src="https://img.shields.io/badge/Native%20AOT-enabled-0A0A0A?style=for-the-badge" />
  <img alt="Pushover" src="https://img.shields.io/badge/Notifications-Pushover-F36C3D?style=for-the-badge" />
</p>

<p align="center">
  Surveillance réseau légère et autonome pour superviser des hôtes IP et des endpoints TCP,
  avec alertes Pushover, persistance d'état, journalisation sur disque et exécution conteneurisée.
</p>

---

## ✨ Vue d'ensemble

**NetworkMonitor** est une application console .NET orientée supervision simple, robuste et déployable partout.

Elle permet de :

- surveiller une ou plusieurs **adresses IP** via `ping`
- surveiller un ou plusieurs **services TCP** via `host:port`
- déclencher des **notifications Pushover** lors d'une panne, d'une reprise et d'une indisponibilité prolongée
- envoyer une **notification Pushover au démarrage et à l'arrêt** du service
- **mémoriser l'état** des cibles dans un stockage persistant
- produire des **logs console + fichiers**
- piloter la fréquence d'exécution via **intervalle** ou **expression CRON**
- être exécutée localement ou via **Docker**

Le projet cible **.NET 11** et active la **publication AOT** pour des déploiements compacts et rapides.

---

## 🚀 Fonctionnalités

### Surveillance ICMP
- Lecture des cibles via `PING_TARGETS`
- Jusqu'à **3 tentatives de ping** par cycle de vérification
- Passage en état **DOWN** après **3 cycles échoués consécutifs**
- Notification de **recovery** dès qu'une cible redevient joignable

### Surveillance TCP
- Lecture des cibles via `TCP_TARGETS`
- Vérification de disponibilité sur des couples `host:port`
- Jusqu'à **3 tentatives de connexion** par cycle
- Détection d'indisponibilité et notification de reprise

### Alerting intelligent
- Notifications **Pushover** avec niveaux de priorité adaptés
- Notification de **démarrage** avec son dédié configurable
- Notification d'**arrêt** avec son dédié configurable et motif de terminaison
- Relance en **urgence** si un incident dure dans le temps
- Mécanisme de **snooze automatique** lorsqu'une alerte urgente est acquittée

### Résilience intégrée
- **Circuit breaker** temporaire après détection d'une panne
- **Persistance d'état** dans `state.json`
- Conservation du contexte entre redémarrages

### Observabilité
- Logs temps réel dans la console
- Fichiers de logs journaliers dans `DATA_DIR/logs`

---

## 🧱 Stack technique

- **.NET 11**
- **C#**
- **Cronos** pour la planification CRON
- **Microsoft.Extensions.Logging** pour la journalisation
- **Pushover API** pour les notifications
- **Docker** pour le packaging et l'exécution
- **Native AOT** pour la publication

---

## 📦 Structure du projet

```text
NetworkMonitor/
├─ NetworkMonitor/
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
│  └─ NetworkMonitor.csproj
└─ README.md
```

---

## ⚙️ Configuration

La configuration se fait uniquement par **variables d'environnement**.

### Variables disponibles

| Variable | Description | Exemple | Valeur par défaut |
|---|---|---|---|
| `PING_TARGETS` | Liste d'IPs à surveiller, séparées par des virgules | `192.168.1.1,8.8.8.8` | vide |
| `TCP_TARGETS` | Liste d'endpoints TCP `host:port`, séparés par des virgules | `google.com:443,192.168.1.10:22` | vide |
| `SCHEDULE_INTERVAL_SECONDS` | Intervalle entre deux cycles de vérification | `10` | `10` |
| `SCHEDULE_CRON` | Expression CRON pour planifier les vérifications | `*/3 * * * *` | non définie |
| `PUSHOVER_TOKEN` | Token d'application Pushover | `xxxxx` | vide |
| `PUSHOVER_USER` | Clé utilisateur ou groupe Pushover | `xxxxx` | vide |
| `PUSHOVER_STARTUP_SOUND` | Son Pushover utilisé pour la notification de démarrage | `cosmic` | `cosmic` |
| `PUSHOVER_SHUTDOWN_SOUND` | Son Pushover utilisé pour la notification d'arrêt | `falling` | `falling` |
| `SNOOZE_DAYS` | Nombre de jours de suspension après acquittement d'une alerte urgente | `1` | `1` |
| `DATA_DIR` | Répertoire de persistance (`state.json`, logs) | `/data` | `.` |
| `APP_VERSION` | Version affichée dans les logs / conteneur | `1.4.4` | `inconnue` |

### Priorité entre intervalle et CRON

Le comportement actuel est le suivant :

1. si `SCHEDULE_CRON` est défini, il est utilisé
2. sinon, `SCHEDULE_INTERVAL_SECONDS` est utilisé
3. si rien n'est défini, l'application tourne toutes les **10 secondes**

---

## 🔔 Comportement des alertes

### Ping
Pour une cible IP :

- un cycle effectue jusqu'à **3 tentatives de ping**
- après **3 cycles échoués consécutifs**, la cible passe en **DOWN**
- une notification `🔴 DOWN` est envoyée
- si la panne dure plus de **5 minutes**, une alerte `🚨 STILL DOWN` est envoyée avec priorité urgente
- dès que la cible répond à nouveau, une notification `🟢 RECOVERY` est envoyée

### TCP
Pour une cible TCP :

- un cycle effectue jusqu'à **3 tentatives de connexion**
- si la vérification échoue, la cible est marquée **DOWN**
- une notification `🔴 DOWN` est envoyée
- si la panne dure plus de **5 minutes**, une alerte `🚨 STILL DOWN` est envoyée avec priorité urgente
- dès qu'une connexion réussit, une notification `🟢 RECOVERY` est envoyée

### Snooze Pushover
Lorsqu'une notification urgente est acquittée côté Pushover :

- l'application détecte l'acquittement via le `receipt`
- les notifications sont suspendues pendant `SNOOZE_DAYS`
- la date de fin de snooze est persistée dans le stockage d'état

### Notifications de cycle de vie
Au démarrage du programme :

- une notification `🚀 NetworkMonitor démarré` est envoyée
- le message contient la version, le rythme d'exécution et le nombre de cibles surveillées
- le son utilisé est `PUSHOVER_STARTUP_SOUND` (par défaut : `cosmic`)

À l'arrêt du programme :

- une notification `🛑 NetworkMonitor arrêté` est envoyée
- le message contient le **motif d'arrêt** lorsque celui-ci est connu
- le son utilisé est `PUSHOVER_SHUTDOWN_SOUND` (par défaut : `falling`)

Motifs d'arrêt actuellement distingués :

- arrêt normal
- arrêt manuel demandé depuis la console
- interruption terminal (`SIGINT`)
- arrêt Docker / système (`SIGTERM`)
- exception inattendue

---

## 🗂️ Persistance et fichiers générés

Le répertoire défini par `DATA_DIR` contient :

### `state.json`
Stocke :
- l'état courant des moniteurs
- la date de début d'incident
- la date de fin de snooze des notifications

### `logs/`
Contient un fichier journalier au format :

```text
networkmonitor-YYYY-MM-DD.log
```

Exemple d'arborescence :

```text
/data/
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

> Le projet cible actuellement `net11.0`.

### Exécution locale

Depuis la racine du dépôt :

```bash
dotnet run --project NetworkMonitor/NetworkMonitor.csproj
```

Exemple PowerShell :

```powershell
$env:PING_TARGETS="8.8.8.8,1.1.1.1"
$env:TCP_TARGETS="google.com:443,localhost:22"
$env:SCHEDULE_INTERVAL_SECONDS="15"
$env:PUSHOVER_TOKEN="your-token"
$env:PUSHOVER_USER="your-user"
$env:DATA_DIR=".data"
dotnet run --project .\NetworkMonitor\NetworkMonitor.csproj
```

---

## 🐳 Utilisation avec Docker

Le projet contient déjà un `Dockerfile` prêt à l'emploi.

### Build

```bash
docker build -t networkmonitor .
```

### Run

```bash
docker run -d \
  --name networkmonitor \
  -e PING_TARGETS="8.8.8.8,1.1.1.1" \
  -e TCP_TARGETS="google.com:443,192.168.1.10:22" \
  -e SCHEDULE_CRON="*/3 * * * *" \
  -e PUSHOVER_TOKEN="your-token" \
  -e PUSHOVER_USER="your-user" \
  -e PUSHOVER_STARTUP_SOUND="cosmic" \
  -e PUSHOVER_SHUTDOWN_SOUND="falling" \
  -e SNOOZE_DAYS="1" \
  -e DATA_DIR="/data" \
  -v networkmonitor-data:/data \
  networkmonitor
```

> Sous Docker, l'arrêt via `docker stop` envoie un `SIGTERM`. L'application intercepte ce signal pour effectuer un arrêt propre et envoyer la notification Pushover de fin.

### Exemple Docker Compose

```yaml
services:
  networkmonitor:
    build: .
    container_name: networkmonitor
    restart: unless-stopped
    environment:
      PING_TARGETS: "8.8.8.8,1.1.1.1"
      TCP_TARGETS: "google.com:443,192.168.1.10:22"
      SCHEDULE_CRON: "*/3 * * * *"
      PUSHOVER_TOKEN: "your-token"
      PUSHOVER_USER: "your-user"
      PUSHOVER_STARTUP_SOUND: "cosmic"
      PUSHOVER_SHUTDOWN_SOUND: "falling"
      SNOOZE_DAYS: "1"
      DATA_DIR: "/data"
      APP_VERSION: "1.4.4"
    volumes:
      - networkmonitor-data:/data

volumes:
  networkmonitor-data:
```

---

## 🧪 Exemples de configuration

### 1. Surveiller des IPs uniquement

```env
PING_TARGETS=192.168.1.1,8.8.8.8,1.1.1.1
SCHEDULE_INTERVAL_SECONDS=10
DATA_DIR=/data
```

### 2. Surveiller des services TCP uniquement

```env
TCP_TARGETS=google.com:443,localhost:5432,192.168.1.20:22
SCHEDULE_INTERVAL_SECONDS=30
DATA_DIR=/data
```

### 3. Utiliser une planification CRON

```env
PING_TARGETS=8.8.8.8
TCP_TARGETS=google.com:443
SCHEDULE_CRON=*/5 * * * *
DATA_DIR=/data
```

---

## 🕒 Exemples d'expressions CRON

Le projet convertit les expressions CRON en description lisible dans les logs.

| Expression | Interprétation |
|---|---|
| `*/3 * * * *` | toutes les 3 minutes |
| `0 */2 * * *` | toutes les 2 heures |
| `0 8 * * 1` | chaque lundi à 08h00 |
| `0 0 1 * *` | le 1er de chaque mois à minuit |
| `*/30 * * * * *` | toutes les 30 secondes |

---

## 🪵 Logs et exploitation

Au démarrage, l'application journalise notamment :

- la **version applicative**
- la **description de la planification**
- les événements `DOWN`, `RECOVERY` et `STILL DOWN`
- les erreurs d'envoi de notifications
- les périodes de **snooze**

Cela rend l'application simple à intégrer dans :

- un serveur domestique / homelab
- un mini VPS
- un conteneur Docker sur NAS
- une plateforme d'hébergement légère

---

## 🔐 Notes d'exploitation

- si `PING_TARGETS` est vide, l'application démarre mais journalise un avertissement
- si `TCP_TARGETS` est vide, l'application démarre également avec avertissement
- sans `PUSHOVER_TOKEN` et `PUSHOVER_USER`, les tentatives d'envoi de notifications échoueront
- `DATA_DIR` doit être persistant en environnement conteneurisé pour conserver l'état et les logs

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
- vérification de disponibilité Internet / DNS publics
- contrôle d'ouverture de ports critiques
- surveillance légère de services auto-hébergés
- alerting simple sans stack de supervision lourde

---

## 🤝 Contribution

Les contributions sont les bienvenues pour :

- enrichir les types de sondes
- améliorer la stratégie de retry/circuit breaker
- ajouter des tests automatisés
- proposer des intégrations de notifications supplémentaires
- améliorer l'expérience Docker / CI

---

## 📄 Licence

Aucune licence n'est actuellement documentée dans le dépôt.

Si vous souhaitez ouvrir clairement l'usage du projet, ajoutez un fichier `LICENSE` adapté.

---

## ❤️ Résumé

**NetworkMonitor** est un moniteur réseau minimaliste, pratique et facile à déployer, pensé pour ceux qui veulent :

- un binaire léger
- peu de configuration
- des alertes utiles
- une exécution fiable en local ou en conteneur

Si vous cherchez une solution simple de **monitoring réseau + notifications Pushover**, ce projet fournit une base propre, moderne et efficace.
