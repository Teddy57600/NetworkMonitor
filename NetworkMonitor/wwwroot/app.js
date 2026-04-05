const refreshIntervalMs = 5000;

let refreshTimerId = null;
let currentRefreshIntervalMs = 5000;
let defaultSnoozeDays = 1;

function formatSnoozeDurationLabel(totalMinutes) {
    if (totalMinutes % 1440 === 0) {
        const days = totalMinutes / 1440;
        return `${days} jour${days > 1 ? 's' : ''}`;
    }

    if (totalMinutes % 60 === 0) {
        const hours = totalMinutes / 60;
        return `${hours} heure${hours > 1 ? 's' : ''}`;
    }

    return `${totalMinutes} minute${totalMinutes > 1 ? 's' : ''}`;
}

function getDefaultSnoozeDurationMinutes() {
    return Math.max(1, defaultSnoozeDays) * 24 * 60;
}

function syncSnoozeDurationSelector() {
    const select = document.getElementById('snoozeDurationSelect');
    const hint = document.getElementById('snoozeDurationHint');
    const defaultLabel = formatSnoozeDurationLabel(getDefaultSnoozeDurationMinutes());
    const defaultOption = select.querySelector('option[value=""]');
    defaultOption.textContent = `Valeur par défaut (${defaultLabel})`;
    hint.textContent = `Cette durée sera utilisée par le bouton Snoozer. Valeur par défaut actuelle : ${defaultLabel}.`;
}

function getSelectedSnoozeDuration() {
    const select = document.getElementById('snoozeDurationSelect');
    if (!select.value) {
        return {
            minutes: null,
            label: formatSnoozeDurationLabel(getDefaultSnoozeDurationMinutes())
        };
    }

    return {
        minutes: Math.max(1, Number(select.value)),
        label: select.options[select.selectedIndex]?.textContent ?? formatSnoozeDurationLabel(Number(select.value))
    };
}

function formatDate(value) {
    if (!value) {
        return '—';
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return value;
    }

    return new Intl.DateTimeFormat('fr-FR', {
        dateStyle: 'short',
        timeStyle: 'medium'
    }).format(date);
}

function setText(id, value) {
    document.getElementById(id).textContent = value ?? '—';
}

function setActionStatus(message, isError = false) {
    const node = document.getElementById('actionStatus');
    node.textContent = message;
    node.classList.toggle('error', isError);
}

function updateGlobalHealth(summary) {
    const badge = document.getElementById('globalHealthBadge');
    badge.classList.remove('badge-health-good', 'badge-health-warning', 'badge-health-danger', 'badge-health-neutral');

    if (!summary || summary.total === 0) {
        badge.classList.add('badge-health-neutral');
        badge.textContent = 'Santé globale — aucune cible';
        return;
    }

    if (summary.down > 0) {
        badge.classList.add('badge-health-danger');
        badge.textContent = `Santé globale — critique (${summary.down} DOWN)`;
        return;
    }

    if (summary.snoozed > 0) {
        badge.classList.add('badge-health-warning');
        badge.textContent = `Santé globale — attention (${summary.snoozed} snoozé${summary.snoozed > 1 ? 's' : ''})`;
        return;
    }

    badge.classList.add('badge-health-good');
    badge.textContent = 'Santé globale — OK';
}

function createSourceBadge(source) {
    const badge = document.createElement('span');
    badge.className = 'source-badge';

    switch (source) {
        case 'ENV':
            badge.classList.add('source-env');
            break;
        case 'YAML':
            badge.classList.add('source-yaml');
            break;
        case 'ENV + YAML':
            badge.classList.add('source-both');
            break;
        default:
            badge.classList.add('source-unknown');
            break;
    }

    badge.textContent = source || 'Inconnue';
    return badge;
}

function scheduleNextRefresh() {
    if (refreshTimerId) {
        clearTimeout(refreshTimerId);
    }

    refreshTimerId = window.setTimeout(refresh, currentRefreshIntervalMs);
}

async function postAction(url) {
    const response = await fetch(url, {
        method: 'POST'
    });

    const payload = await response.json();
    if (!response.ok || payload.success === false) {
        throw new Error(payload.message ?? `HTTP ${response.status}`);
    }

    return payload;
}

async function requestImmediateCheck() {
    const button = document.getElementById('checkNowButton');
    button.disabled = true;
    setActionStatus('Demande de cycle immédiat en cours...');

    try {
        const payload = await postAction('/api/actions/check-now');
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la demande : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function snoozeMonitor(key, button) {
    const snoozeDuration = getSelectedSnoozeDuration();
    if (!window.confirm(`Snoozer '${key}' pendant ${snoozeDuration.label} ?`)) {
        return;
    }

    button.disabled = true;
    setActionStatus(`Activation du snooze pour ${key} (${snoozeDuration.label})...`);

    try {
        const durationQuery = snoozeDuration.minutes === null
            ? ''
            : `&durationMinutes=${encodeURIComponent(snoozeDuration.minutes)}`;
        const payload = await postAction(`/api/actions/snooze?key=${encodeURIComponent(key)}${durationQuery}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec du snooze : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function clearSnooze(key, button) {
    button.disabled = true;
    setActionStatus(`Suppression du snooze pour ${key}...`);

    try {
        const payload = await postAction(`/api/actions/clear-snooze?key=${encodeURIComponent(key)}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du snooze : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function removePingTarget(target, button) {
    if (!window.confirm(`Supprimer la cible ping '${target}' du fichier YAML ?`)) {
        return;
    }

    button.disabled = true;
    setActionStatus(`Suppression de la cible ping ${target}...`);

    try {
        const payload = await postAction(`/api/actions/remove-ping?target=${encodeURIComponent(target)}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du ping : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function removeTcpTarget(key, button) {
    if (!window.confirm(`Supprimer le test TCP '${key}' du fichier YAML ?`)) {
        return;
    }

    const separatorIndex = key.lastIndexOf(':');
    if (separatorIndex <= 0) {
        setActionStatus(`Clé TCP invalide : ${key}`, true);
        return;
    }

    const host = key.slice(0, separatorIndex);
    const port = Number(key.slice(separatorIndex + 1));

    button.disabled = true;
    setActionStatus(`Suppression du test TCP ${key}...`);

    try {
        const payload = await postAction(`/api/actions/remove-tcp?host=${encodeURIComponent(host)}&port=${port}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du test TCP : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function addPingTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const input = document.getElementById('pingTargetInput');
    const target = input.value.trim();
    if (!target) {
        setActionStatus('La cible ping est obligatoire.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout de la cible ping ${target}...`);

    try {
        const payload = await postAction(`/api/actions/add-ping?target=${encodeURIComponent(target)}`);
        setActionStatus(payload.message);
        form.reset();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du ping : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function addTcpTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const host = document.getElementById('tcpHostInput').value.trim();
    const port = Number(document.getElementById('tcpPortInput').value);

    if (!host || !Number.isInteger(port) || port <= 0) {
        setActionStatus('L’hôte TCP et un port valide sont obligatoires.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout du test TCP ${host}:${port}...`);

    try {
        const payload = await postAction(`/api/actions/add-tcp?host=${encodeURIComponent(host)}&port=${port}`);
        setActionStatus(payload.message);
        form.reset();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du test TCP : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

function renderMonitorList(containerId, monitors) {
    const container = document.getElementById(containerId);
    container.innerHTML = '';

    if (!monitors || monitors.length === 0) {
        const emptyState = document.createElement('div');
        emptyState.className = 'empty-state';
        emptyState.textContent = 'Aucun moniteur configuré.';
        container.appendChild(emptyState);
        return;
    }

    const template = document.getElementById('monitor-template');

    monitors.forEach((monitor) => {
        const node = template.content.firstElementChild.cloneNode(true);
        node.querySelector('.monitor-type').textContent = monitor.type;
        node.querySelector('.monitor-name').textContent = monitor.displayName;

        const state = node.querySelector('.monitor-state');
        state.textContent = monitor.status;
        state.classList.add(monitor.isDown ? 'down' : 'up');
        if (monitor.snoozeUntil) {
            state.classList.add('snoozed');
            state.textContent += ' • snooze';
        }

        const actions = node.querySelector('.monitor-actions');

        if (!monitor.snoozeUntil) {
            const snoozeButton = document.createElement('button');
            snoozeButton.type = 'button';
            snoozeButton.className = 'action-button secondary';
            snoozeButton.textContent = 'Snoozer';
            snoozeButton.addEventListener('click', () => snoozeMonitor(monitor.key, snoozeButton));
            actions.appendChild(snoozeButton);
        }

        if (monitor.source !== 'ENV') {
            const removeButton = document.createElement('button');
            removeButton.type = 'button';
            removeButton.className = 'action-button danger';
            removeButton.textContent = monitor.type === 'TCP' ? 'Supprimer le test' : 'Supprimer la cible';
            removeButton.addEventListener('click', () => {
                if (monitor.type === 'TCP') {
                    removeTcpTarget(monitor.key, removeButton);
                    return;
                }

                removePingTarget(monitor.key, removeButton);
            });
            actions.appendChild(removeButton);
        }

        if (monitor.snoozeUntil) {
            const clearSnoozeButton = document.createElement('button');
            clearSnoozeButton.type = 'button';
            clearSnoozeButton.className = 'action-button ghost';
            clearSnoozeButton.textContent = 'Supprimer le snooze';
            clearSnoozeButton.addEventListener('click', () => clearSnooze(monitor.key, clearSnoozeButton));
            actions.appendChild(clearSnoozeButton);
        }

        const meta = node.querySelector('.monitor-meta');
        const rows = [
            ['Source', createSourceBadge(monitor.source)],
            ['Dernier contrôle', formatDate(monitor.lastCheckAt)],
            ['Dernier succès', formatDate(monitor.lastSuccessAt)],
            ['Dernier échec', formatDate(monitor.lastFailureAt)],
            ['Durée dernier test', monitor.lastDurationMs ? `${monitor.lastDurationMs.toFixed(0)} ms` : '—'],
            ['Échecs consécutifs', String(monitor.failCount)],
            ['DOWN depuis', formatDate(monitor.downSince)],
            ['Circuit ouvert jusqu’à', formatDate(monitor.circuitOpenUntil)],
            ['Snooze jusqu’à', formatDate(monitor.snoozeUntil)]
        ];

        rows.forEach(([label, value]) => {
            const wrapper = document.createElement('div');
            const dt = document.createElement('dt');
            const dd = document.createElement('dd');
            dt.textContent = label;
            if (value instanceof Node) {
                dd.appendChild(value);
            }
            else {
                dd.textContent = value;
            }
            wrapper.append(dt, dd);
            meta.appendChild(wrapper);
        });

        container.appendChild(node);
    });
}

async function refresh() {
    try {
        const response = await fetch('/api/dashboard', { cache: 'no-store' });
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const data = await response.json();
        currentRefreshIntervalMs = Math.max(1, Number(data.refreshIntervalSeconds ?? 5)) * 1000;
        defaultSnoozeDays = Math.max(1, Number(data.defaultSnoozeDays ?? 1));
        syncSnoozeDurationSelector();

        updateGlobalHealth(data.summary);
        setText('subtitle', `${data.summary.total} moniteur(s) • ${data.summary.up} UP • ${data.summary.down} DOWN`);
        setText('generatedAt', `MàJ ${formatDate(data.generatedAt)}`);
        setText('timeZone', data.timeZone);
        setText('totalCount', data.summary.total);
        setText('upCount', data.summary.up);
        setText('downCount', data.summary.down);
        setText('snoozedCount', data.summary.snoozed);
        setText('version', data.version);
        setText('schedule', data.schedule);
        setText('configPath', data.configPath);
        setText('configVersion', data.configVersion);
        setText('startedAt', formatDate(data.startedAt));
        setText('refreshInterval', `${Math.round(currentRefreshIntervalMs / 1000)} s`);

        renderMonitorList('pingMonitors', data.pingMonitors);
        renderMonitorList('tcpMonitors', data.tcpMonitors);
    }
    catch (error) {
        setText('subtitle', `Erreur de chargement du tableau de bord : ${error.message}`);
    }
    finally {
        scheduleNextRefresh();
    }
}

document.getElementById('checkNowButton').addEventListener('click', requestImmediateCheck);
document.getElementById('pingTargetForm').addEventListener('submit', addPingTarget);
document.getElementById('tcpTargetForm').addEventListener('submit', addTcpTarget);
refresh();
