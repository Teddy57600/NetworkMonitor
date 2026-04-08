const refreshIntervalMs = 5000;
const refreshIntervalStorageKey = 'networkmonitor-refresh-interval';

let refreshTimerId = null;
let currentRefreshIntervalMs = 5000;
let serverRefreshIntervalMs = 5000;
let defaultSnoozeDays = 1;
let lastSummary = null;
let lastIncidents = [];
let hasLoadedConfigEditor = false;
let lastDashboardData = {
    pingMonitors: [],
    tcpMonitors: [],
    httpMonitors: [],
    dnsMonitors: []
};

function formatRefreshIntervalLabel(milliseconds) {
    const totalSeconds = Math.max(1, Math.round(milliseconds / 1000));
    if (totalSeconds % 60 === 0) {
        const minutes = totalSeconds / 60;
        return `${minutes} minute${minutes > 1 ? 's' : ''}`;
    }

    return `${totalSeconds} seconde${totalSeconds > 1 ? 's' : ''}`;
}

function getRefreshPreference() {
    return localStorage.getItem(refreshIntervalStorageKey) ?? 'auto';
}

function syncRefreshIntervalSelector() {
    const select = document.getElementById('refreshIntervalSelect');
    const hint = document.getElementById('refreshIntervalHint');
    if (!select || !hint) {
        return;
    }

    const preference = getRefreshPreference();
    select.value = Array.from(select.options).some(option => option.value === preference) ? preference : 'auto';
    hint.textContent = `Fréquence configurée côté service : ${formatRefreshIntervalLabel(serverRefreshIntervalMs)}.`;
}

function applyRefreshIntervalPreference() {
    const preference = getRefreshPreference();
    currentRefreshIntervalMs = preference === 'auto'
        ? serverRefreshIntervalMs
        : Math.max(1000, Number(preference) || serverRefreshIntervalMs);
}

function updateRefreshIntervalDisplay() {
    const preference = getRefreshPreference();
    const suffix = preference === 'auto' ? 'auto' : 'manuel';
    setText('refreshInterval', `${formatRefreshIntervalLabel(currentRefreshIntervalMs)} (${suffix})`);
}

function getIncidentFilters() {
    return {
        status: document.getElementById('incidentStatusFilter')?.value ?? 'all',
        type: document.getElementById('incidentTypeFilter')?.value ?? 'all',
        search: document.getElementById('incidentSearchInput')?.value.trim().toLowerCase() ?? ''
    };
}

function filterIncidents(incidents) {
    const filters = getIncidentFilters();

    return incidents.filter(incident => {
        if (filters.status === 'open' && !incident.isOpen) {
            return false;
        }

        if (filters.status === 'resolved' && incident.isOpen) {
            return false;
        }

        if (filters.type !== 'all' && incident.type !== filters.type) {
            return false;
        }

        if (!filters.search) {
            return true;
        }

        const haystack = `${incident.displayName} ${incident.key} ${incident.type}`.toLowerCase();
        return haystack.includes(filters.search);
    });
}

function rerenderMonitorLists() {
    renderMonitorList('pingMonitors', lastDashboardData.pingMonitors);
    renderMonitorList('tcpMonitors', lastDashboardData.tcpMonitors);
    renderMonitorList('httpMonitors', lastDashboardData.httpMonitors);
    renderMonitorList('dnsMonitors', lastDashboardData.dnsMonitors);
}

function updateIncidentFilterSummary(visibleCount, totalCount) {
    const summary = document.getElementById('incidentFilterSummary');
    if (!summary) {
        return;
    }

    summary.textContent = visibleCount === totalCount
        ? `${totalCount} incident${totalCount > 1 ? 's' : ''}`
        : `${visibleCount} / ${totalCount} incident${totalCount > 1 ? 's' : ''}`;
}

function updatePageChrome(summary) {
    lastSummary = summary;

    if (!summary || summary.total === 0) {
        document.title = 'NetworkMonitor Dashboard';
        setDynamicFavicon('neutral');
        return;
    }

    if (summary.down > 0) {
        document.title = `(${summary.down}) DOWN • NetworkMonitor Dashboard`;
        setDynamicFavicon('danger');
        return;
    }

    if (summary.snoozed > 0) {
        document.title = `(${summary.snoozed}) Snoozé • NetworkMonitor Dashboard`;
        setDynamicFavicon('warning');
        return;
    }

    document.title = 'OK • NetworkMonitor Dashboard';
    setDynamicFavicon('good');
}

function setDynamicFavicon(state) {
    const favicon = document.getElementById('appFavicon');
    if (!favicon) {
        return;
    }

    const theme = window.NetworkMonitorTheme?.getCurrentTheme?.() ?? 'dark';
    const colors = {
        good: { pulse: '#22c55e', frame: '#0f172a', screen: theme === 'light' ? '#f8fafc' : '#e5eefb' },
        warning: { pulse: '#f59e0b', frame: '#7c2d12', screen: theme === 'light' ? '#fff7ed' : '#ffedd5' },
        danger: { pulse: '#ef4444', frame: '#7f1d1d', screen: theme === 'light' ? '#fef2f2' : '#fee2e2' },
        neutral: { pulse: '#94a3b8', frame: '#334155', screen: theme === 'light' ? '#f8fafc' : '#e2e8f0' }
    };

    const palette = colors[state] ?? colors.neutral;
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect x="4" y="4" width="56" height="56" rx="16" fill="${theme === 'light' ? '#dbeafe' : '#172554'}"/><rect x="14" y="16" width="36" height="24" rx="5" fill="${palette.screen}" opacity="0.96"/><rect x="18" y="20" width="28" height="16" rx="3" fill="${palette.frame}"/><path d="M20 29h5l3-5 4 10 4-7 2 2h6" fill="none" stroke="${palette.pulse}" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/><path d="M22 47h20" stroke="${theme === 'light' ? '#334155' : '#cbd5e1'}" stroke-width="4" stroke-linecap="round"/><path d="M16 52h32" stroke="${theme === 'light' ? '#64748b' : '#94a3b8'}" stroke-width="4" stroke-linecap="round"/></svg>`;
    favicon.href = `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

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

function formatIncidentDuration(startedAt, resolvedAt) {
    if (!startedAt) {
        return '—';
    }

    const start = new Date(startedAt);
    const end = resolvedAt ? new Date(resolvedAt) : new Date();
    const totalSeconds = Math.max(0, Math.round((end - start) / 1000));
    const days = Math.floor(totalSeconds / 86400);
    const hours = Math.floor((totalSeconds % 86400) / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);

    const parts = [];
    if (days > 0) {
        parts.push(`${days}j`);
    }

    if (hours > 0) {
        parts.push(`${hours}h`);
    }

    if (minutes > 0 || parts.length === 0) {
        parts.push(`${minutes}min`);
    }

    return parts.join(' ');
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

async function removeHttpTarget(url, button) {
    if (!window.confirm(`Supprimer le test HTTP '${url}' du fichier YAML ?`)) {
        return;
    }

    button.disabled = true;
    setActionStatus(`Suppression du test HTTP ${url}...`);

    try {
        const payload = await postAction(`/api/actions/remove-http?url=${encodeURIComponent(url)}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du test HTTP : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function removeDnsTarget(host, button) {
    if (!window.confirm(`Supprimer le test DNS '${host}' du fichier YAML ?`)) {
        return;
    }

    button.disabled = true;
    setActionStatus(`Suppression du test DNS ${host}...`);

    try {
        const payload = await postAction(`/api/actions/remove-dns?host=${encodeURIComponent(host)}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du test DNS : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function removeDnsReverseTarget(ip, button) {
    if (!window.confirm(`Supprimer le reverse DNS '${ip}' du fichier YAML ?`)) {
        return;
    }

    button.disabled = true;
    setActionStatus(`Suppression du reverse DNS ${ip}...`);

    try {
        const payload = await postAction(`/api/actions/remove-dns-reverse?ip=${encodeURIComponent(ip)}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du reverse DNS : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function addHttpTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const url = document.getElementById('httpUrlInput').value.trim();
    const expectedStatusCodeRaw = document.getElementById('httpStatusCodeInput').value.trim();
    const containsText = document.getElementById('httpContainsTextInput').value.trim();

    if (!url) {
        setActionStatus('L’URL HTTP/HTTPS est obligatoire.', true);
        return;
    }

    const expectedStatusCode = expectedStatusCodeRaw ? Number(expectedStatusCodeRaw) : null;
    if (expectedStatusCodeRaw && (!Number.isInteger(expectedStatusCode) || expectedStatusCode < 100 || expectedStatusCode > 599)) {
        setActionStatus('Le code HTTP attendu doit être compris entre 100 et 599.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout du test HTTP ${url}...`);

    try {
        const statusQuery = expectedStatusCode === null ? '' : `&expectedStatusCode=${expectedStatusCode}`;
        const textQuery = containsText ? `&containsText=${encodeURIComponent(containsText)}` : '';
        const payload = await postAction(`/api/actions/add-http?url=${encodeURIComponent(url)}${statusQuery}${textQuery}`);
        setActionStatus(payload.message);
        form.reset();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du test HTTP : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function addDnsTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const host = document.getElementById('dnsHostInput').value.trim();
    const expectedAddress = document.getElementById('dnsExpectedAddressInput').value.trim();

    if (!host) {
        setActionStatus('Le nom de domaine est obligatoire.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout du test DNS ${host}...`);

    try {
        const addressQuery = expectedAddress ? `&expectedAddress=${encodeURIComponent(expectedAddress)}` : '';
        const payload = await postAction(`/api/actions/add-dns?host=${encodeURIComponent(host)}${addressQuery}`);
        setActionStatus(payload.message);
        form.reset();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du test DNS : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function addDnsReverseTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const ip = document.getElementById('dnsReverseIpInput').value.trim();
    const expectedHost = document.getElementById('dnsExpectedHostInput').value.trim();

    if (!ip) {
        setActionStatus('L’adresse IP du reverse DNS est obligatoire.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout du reverse DNS ${ip}...`);

    try {
        const hostQuery = expectedHost ? `&expectedHost=${encodeURIComponent(expectedHost)}` : '';
        const payload = await postAction(`/api/actions/add-dns-reverse?ip=${encodeURIComponent(ip)}${hostQuery}`);
        setActionStatus(payload.message);
        form.reset();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du reverse DNS : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

function renderIncidentList(containerId, incidents) {
    const container = document.getElementById(containerId);
    container.innerHTML = '';

    const filteredIncidents = filterIncidents(incidents ?? []);
    updateIncidentFilterSummary(filteredIncidents.length, incidents?.length ?? 0);

    if (!filteredIncidents.length) {
        const emptyState = document.createElement('div');
        emptyState.className = 'empty-state';
        emptyState.textContent = incidents?.length ? 'Aucun incident ne correspond aux filtres.' : 'Aucun incident récent.';
        container.appendChild(emptyState);
        return;
    }

    const template = document.getElementById('incident-template');

    filteredIncidents.forEach((incident) => {
        const node = template.content.firstElementChild.cloneNode(true);
        node.querySelector('.incident-type').textContent = incident.type;
        node.querySelector('.incident-name').textContent = incident.displayName;

        const status = node.querySelector('.incident-status');
        status.textContent = incident.isOpen ? 'En cours' : 'Résolu';
        status.classList.add(incident.isOpen ? 'open' : 'resolved');

        const meta = node.querySelector('.incident-meta');
        const rows = [
            ['Clé', incident.key],
            ['Début', formatDate(incident.startedAt)],
            ['Fin', incident.resolvedAt ? formatDate(incident.resolvedAt) : 'Toujours actif'],
            ['Durée', formatIncidentDuration(incident.startedAt, incident.resolvedAt)]
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

function getMonitorFilters() {
    return {
        status: document.getElementById('monitorStatusFilter')?.value ?? 'all',
        source: document.getElementById('monitorSourceFilter')?.value ?? 'all',
        sort: document.getElementById('monitorSortSelect')?.value ?? 'state',
        search: document.getElementById('monitorSearchInput')?.value.trim().toLowerCase() ?? ''
    };
}

function getDowntimeValue(monitor) {
    if (!monitor.downSince) {
        return -1;
    }

    return Date.now() - new Date(monitor.downSince).getTime();
}

function compareMonitorState(left, right) {
    const getRank = monitor => {
        if (monitor.isDown) {
            return 0;
        }

        if (monitor.snoozeUntil) {
            return 1;
        }

        return 2;
    };

    return getRank(left) - getRank(right);
}

function applyMonitorFiltersAndSort(monitors) {
    const filters = getMonitorFilters();
    const filtered = (monitors ?? []).filter(monitor => {
        if (filters.status === 'down' && !monitor.isDown) {
            return false;
        }

        if (filters.status === 'up' && monitor.isDown) {
            return false;
        }

        if (filters.status === 'snoozed' && !monitor.snoozeUntil) {
            return false;
        }

        if (filters.source !== 'all' && monitor.source !== filters.source) {
            return false;
        }

        if (!filters.search) {
            return true;
        }

        const haystack = `${monitor.displayName} ${monitor.type} ${monitor.source}`.toLowerCase();
        return haystack.includes(filters.search);
    });

    filtered.sort((left, right) => {
        switch (filters.sort) {
            case 'name':
                return left.displayName.localeCompare(right.displayName, 'fr', { sensitivity: 'base' });
            case 'downtime':
                return getDowntimeValue(right) - getDowntimeValue(left)
                    || left.displayName.localeCompare(right.displayName, 'fr', { sensitivity: 'base' });
            case 'source':
                return left.source.localeCompare(right.source, 'fr', { sensitivity: 'base' })
                    || left.displayName.localeCompare(right.displayName, 'fr', { sensitivity: 'base' });
            case 'state':
            default:
                return compareMonitorState(left, right)
                    || left.displayName.localeCompare(right.displayName, 'fr', { sensitivity: 'base' });
        }
    });

    return filtered;
}

async function loadConfigEditor() {
    const editor = document.getElementById('configEditor');
    const pathBadge = document.getElementById('configPathDisplay');

    try {
        const response = await fetch('/api/config/raw', { cache: 'no-store' });
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const payload = await response.json();
        editor.value = payload.content ?? '';
        pathBadge.textContent = payload.path ?? '—';
        hasLoadedConfigEditor = true;
    }
    catch (error) {
        setActionStatus(`Échec du chargement de la configuration : ${error.message}`, true);
    }
}

async function saveConfigEditor() {
    const button = document.getElementById('saveConfigButton');
    const editor = document.getElementById('configEditor');
    button.disabled = true;
    setActionStatus('Enregistrement de la configuration YAML...');

    try {
        const formData = new FormData();
        formData.append('content', editor.value ?? '');

        const response = await fetch('/api/config/raw', {
            method: 'POST',
            body: formData
        });

        const payload = await response.json();
        if (!response.ok || payload.success === false) {
            throw new Error(payload.message ?? `HTTP ${response.status}`);
        }

        setActionStatus(payload.message);
        await loadConfigEditor();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'enregistrement du YAML : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function importConfigFile(event) {
    const input = event.currentTarget;
    const file = input.files?.[0];
    if (!file) {
        return;
    }

    setActionStatus(`Import du fichier ${file.name}...`);

    try {
        const formData = new FormData();
        formData.append('configFile', file);

        const response = await fetch('/api/config/import', {
            method: 'POST',
            body: formData
        });

        const payload = await response.json();
        if (!response.ok || payload.success === false) {
            throw new Error(payload.message ?? `HTTP ${response.status}`);
        }

        setActionStatus(payload.message);
        await loadConfigEditor();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'import du YAML : ${error.message}`, true);
    }
    finally {
        input.value = '';
    }
}

function renderMonitorList(containerId, monitors) {
    const container = document.getElementById(containerId);
    container.innerHTML = '';

    const visibleMonitors = applyMonitorFiltersAndSort(monitors ?? []);

    if (!visibleMonitors.length) {
        const emptyState = document.createElement('div');
        emptyState.className = 'empty-state';
        emptyState.textContent = (monitors?.length ?? 0) > 0
            ? 'Aucun moniteur ne correspond aux filtres.'
            : 'Aucun moniteur configuré.';
        container.appendChild(emptyState);
        return;
    }

    const template = document.getElementById('monitor-template');

    visibleMonitors.forEach((monitor) => {
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
            removeButton.textContent = monitor.type === 'Ping' ? 'Supprimer la cible' : 'Supprimer le test';
            removeButton.addEventListener('click', () => {
                if (monitor.type === 'TCP') {
                    removeTcpTarget(monitor.key, removeButton);
                    return;
                }

                if (monitor.type === 'HTTP') {
                    removeHttpTarget(monitor.displayName, removeButton);
                    return;
                }

                if (monitor.type === 'DNS') {
                    if (monitor.key.startsWith('DNS:PTR:')) {
                        removeDnsReverseTarget(monitor.key.substring('DNS:PTR:'.length), removeButton);
                        return;
                    }

                    removeDnsTarget(monitor.displayName, removeButton);
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
        serverRefreshIntervalMs = Math.max(1, Number(data.refreshIntervalSeconds ?? 5)) * 1000;
        defaultSnoozeDays = Math.max(1, Number(data.defaultSnoozeDays ?? 1));
        syncRefreshIntervalSelector();
        applyRefreshIntervalPreference();
        syncSnoozeDurationSelector();

        updateGlobalHealth(data.summary);
        updatePageChrome(data.summary);
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
        updateRefreshIntervalDisplay();
        document.getElementById('configPathDisplay').textContent = data.configPath ?? '—';

        lastDashboardData = {
            pingMonitors: data.pingMonitors ?? [],
            tcpMonitors: data.tcpMonitors ?? [],
            httpMonitors: data.httpMonitors ?? [],
            dnsMonitors: data.dnsMonitors ?? []
        };
        rerenderMonitorLists();
        lastIncidents = data.recentIncidents ?? [];
        renderIncidentList('incidentHistory', lastIncidents);

        if (!hasLoadedConfigEditor) {
            await loadConfigEditor();
        }
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
document.getElementById('httpTargetForm').addEventListener('submit', addHttpTarget);
document.getElementById('dnsTargetForm').addEventListener('submit', addDnsTarget);
document.getElementById('dnsReverseTargetForm').addEventListener('submit', addDnsReverseTarget);
document.getElementById('refreshIntervalSelect').addEventListener('change', event => {
    localStorage.setItem(refreshIntervalStorageKey, event.currentTarget.value);
    applyRefreshIntervalPreference();
    updateRefreshIntervalDisplay();
    scheduleNextRefresh();
});
document.getElementById('incidentStatusFilter').addEventListener('change', () => renderIncidentList('incidentHistory', lastIncidents));
document.getElementById('incidentTypeFilter').addEventListener('change', () => renderIncidentList('incidentHistory', lastIncidents));
document.getElementById('incidentSearchInput').addEventListener('input', () => renderIncidentList('incidentHistory', lastIncidents));
document.getElementById('saveConfigButton').addEventListener('click', saveConfigEditor);
document.getElementById('reloadConfigButton').addEventListener('click', loadConfigEditor);
document.getElementById('configImportInput').addEventListener('change', importConfigFile);
document.getElementById('monitorStatusFilter').addEventListener('change', rerenderMonitorLists);
document.getElementById('monitorSourceFilter').addEventListener('change', rerenderMonitorLists);
document.getElementById('monitorSortSelect').addEventListener('change', rerenderMonitorLists);
document.getElementById('monitorSearchInput').addEventListener('input', rerenderMonitorLists);
refresh();
