const refreshIntervalMs = 5000;

let refreshTimerId = null;
let currentRefreshIntervalMs = 5000;

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
        if (monitor.snoozeUntil) {
            const clearSnoozeButton = document.createElement('button');
            clearSnoozeButton.type = 'button';
            clearSnoozeButton.className = 'action-button secondary';
            clearSnoozeButton.textContent = 'Supprimer le snooze';
            clearSnoozeButton.addEventListener('click', () => clearSnooze(monitor.key, clearSnoozeButton));
            actions.appendChild(clearSnoozeButton);
        }

        const meta = node.querySelector('.monitor-meta');
        const rows = [
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
            dd.textContent = value;
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
refresh();
