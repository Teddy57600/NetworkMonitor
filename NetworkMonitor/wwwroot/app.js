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
    dnsMonitors: [],
    tlsMonitors: [],
    dnsRecordMonitors: []
};
const multiSelectFilters = new Map();

function registerMultiSelectFilter(config) {
    const container = document.getElementById(config.id);
    if (!container) {
        return;
    }

    container.innerHTML = '';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'multiselect-filter-button';
    button.textContent = config.placeholder;

    const panel = document.createElement('div');
    panel.className = 'multiselect-filter-panel';
    document.body.appendChild(panel);

    container.append(button);
    multiSelectFilters.set(config.id, {
        ...config,
        container,
        button,
        panel,
        options: []
    });

    button.addEventListener('click', () => {
        const isOpen = !panel.classList.contains('open');
        closeOtherMultiSelectFilters(config.id);
        if (isOpen) {
            positionMultiSelectPanel(config.id);
            panel.classList.add('open');
            container.classList.add('open');
        }
        else {
            panel.classList.remove('open');
            container.classList.remove('open');
        }
    });
}

function positionMultiSelectPanel(filterId) {
    const filter = multiSelectFilters.get(filterId);
    if (!filter) {
        return;
    }

    const rect = filter.button.getBoundingClientRect();
    const panelWidth = Math.max(rect.width, 220);
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - panelWidth - 8));
    filter.panel.style.top = `${rect.bottom + 6}px`;
    filter.panel.style.left = `${left}px`;
    filter.panel.style.width = `${panelWidth}px`;
}

function repositionOpenMultiSelectPanels() {
    multiSelectFilters.forEach((filter, id) => {
        if (filter.panel.classList.contains('open')) {
            positionMultiSelectPanel(id);
        }
    });
}

function closeOtherMultiSelectFilters(exceptId = null) {
    multiSelectFilters.forEach((filter, id) => {
        if (id !== exceptId) {
            filter.panel.classList.remove('open');
            filter.container.classList.remove('open');
        }
    });
}

function setMultiSelectOptions(filterId, options) {
    const filter = multiSelectFilters.get(filterId);
    if (!filter) {
        return;
    }

    const previousSelection = new Set(filter.options.filter(option => option.selected).map(option => option.value));
    filter.options = options.map(option => ({
        ...option,
        selected: previousSelection.size === 0 ? option.selected ?? false : previousSelection.has(option.value)
    }));

    filter.panel.innerHTML = '';

    filter.options.forEach(option => {
        const label = document.createElement('label');
        label.className = 'multiselect-option';

        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.value = option.value;
        checkbox.checked = option.selected;
        checkbox.addEventListener('change', () => {
            option.selected = checkbox.checked;
            updateMultiSelectLabel(filterId);
            filter.onChange?.();
        });

        const text = document.createElement('span');
        text.textContent = option.label;
        label.append(checkbox, text);
        filter.panel.appendChild(label);
    });

    updateMultiSelectLabel(filterId);
}

function updateMultiSelectLabel(filterId) {
    const filter = multiSelectFilters.get(filterId);
    if (!filter) {
        return;
    }

    const selected = filter.options.filter(option => option.selected);
    if (selected.length === 0 || selected.length === filter.options.length) {
        filter.button.textContent = filter.placeholder;
        return;
    }

    if (selected.length === 1) {
        filter.button.textContent = selected[0].label;
        return;
    }

    filter.button.textContent = `${selected.length} sélectionnés`;
}

function getSelectedMultiSelectValues(filterId) {
    const filter = multiSelectFilters.get(filterId);
    if (!filter) {
        return [];
    }

    const selectedValues = filter.options.filter(option => option.selected).map(option => option.value);
    return selectedValues.length === filter.options.length ? [] : selectedValues;
}

function buildOptionLabel(name, count) {
    return `${name} (${count})`;
}

function countBy(items, selector) {
    const counts = new Map();
    (items ?? []).forEach(item => {
        const key = selector(item);
        counts.set(key, (counts.get(key) ?? 0) + 1);
    });
    return counts;
}

function updateMonitorFilterOptions() {
    const allMonitors = [
        ...lastDashboardData.pingMonitors,
        ...lastDashboardData.tcpMonitors,
        ...lastDashboardData.httpMonitors,
        ...lastDashboardData.dnsMonitors,
        ...lastDashboardData.tlsMonitors,
        ...lastDashboardData.dnsRecordMonitors
    ];

    const statusCounts = new Map([
        ['down', allMonitors.filter(monitor => monitor.isDown).length],
        ['up', allMonitors.filter(monitor => !monitor.isDown).length],
        ['snoozed', allMonitors.filter(monitor => !!monitor.snoozeUntil).length]
    ]);
    setMultiSelectOptions('monitorStatusFilter', [
        { value: 'down', label: buildOptionLabel('DOWN', statusCounts.get('down') ?? 0) },
        { value: 'up', label: buildOptionLabel('UP', statusCounts.get('up') ?? 0) },
        { value: 'snoozed', label: buildOptionLabel('Snoozés', statusCounts.get('snoozed') ?? 0) }
    ]);

    const sourceCounts = countBy(allMonitors, monitor => monitor.source);
    setMultiSelectOptions('monitorSourceFilter', [
        { value: 'ENV', label: buildOptionLabel('ENV', sourceCounts.get('ENV') ?? 0) },
        { value: 'YAML', label: buildOptionLabel('YAML', sourceCounts.get('YAML') ?? 0) },
        { value: 'ENV + YAML', label: buildOptionLabel('ENV + YAML', sourceCounts.get('ENV + YAML') ?? 0) }
    ]);

    const typeCounts = countBy(allMonitors, monitor => monitor.type);
    setMultiSelectOptions('monitorTypeFilter', [
        { value: 'Ping', label: buildOptionLabel('Ping', typeCounts.get('Ping') ?? 0) },
        { value: 'TCP', label: buildOptionLabel('TCP', typeCounts.get('TCP') ?? 0) },
        { value: 'HTTP', label: buildOptionLabel('HTTP', typeCounts.get('HTTP') ?? 0) },
        { value: 'DNS', label: buildOptionLabel('DNS', typeCounts.get('DNS') ?? 0) },
        { value: 'TLS', label: buildOptionLabel('TLS', typeCounts.get('TLS') ?? 0) },
        { value: 'DNS Record', label: buildOptionLabel('DNS Record', typeCounts.get('DNS Record') ?? 0) }
    ]);
}

function updateIncidentFilterOptions() {
    const statusCounts = new Map([
        ['open', lastIncidents.filter(incident => incident.isOpen).length],
        ['resolved', lastIncidents.filter(incident => !incident.isOpen).length]
    ]);
    setMultiSelectOptions('incidentStatusFilter', [
        { value: 'open', label: buildOptionLabel('En cours', statusCounts.get('open') ?? 0) },
        { value: 'resolved', label: buildOptionLabel('Résolus', statusCounts.get('resolved') ?? 0) }
    ]);

    const typeCounts = countBy(lastIncidents, incident => incident.type);
    setMultiSelectOptions('incidentTypeFilter', [
        { value: 'Ping', label: buildOptionLabel('Ping', typeCounts.get('Ping') ?? 0) },
        { value: 'TCP', label: buildOptionLabel('TCP', typeCounts.get('TCP') ?? 0) },
        { value: 'HTTP', label: buildOptionLabel('HTTP', typeCounts.get('HTTP') ?? 0) },
        { value: 'DNS', label: buildOptionLabel('DNS', typeCounts.get('DNS') ?? 0) },
        { value: 'TLS', label: buildOptionLabel('TLS', typeCounts.get('TLS') ?? 0) },
        { value: 'DNS Record', label: buildOptionLabel('DNS Record', typeCounts.get('DNS Record') ?? 0) }
    ]);
}

function initializeMultiSelectFilters() {
    registerMultiSelectFilter({ id: 'monitorStatusFilter', placeholder: 'Tous', onChange: rerenderMonitorLists });
    registerMultiSelectFilter({ id: 'monitorSourceFilter', placeholder: 'Toutes', onChange: rerenderMonitorLists });
    registerMultiSelectFilter({ id: 'monitorTypeFilter', placeholder: 'Tous', onChange: rerenderMonitorLists });
    registerMultiSelectFilter({ id: 'incidentStatusFilter', placeholder: 'Tous', onChange: () => renderIncidentList('incidentHistory', lastIncidents) });
    registerMultiSelectFilter({ id: 'incidentTypeFilter', placeholder: 'Tous', onChange: () => renderIncidentList('incidentHistory', lastIncidents) });

    document.addEventListener('click', event => {
        if (!(event.target instanceof Node)) {
            return;
        }

        const clickedInsideFilter = Array.from(multiSelectFilters.values()).some(filter => filter.container.contains(event.target) || filter.panel.contains(event.target));
        if (!clickedInsideFilter) {
            closeOtherMultiSelectFilters();
        }
    });

    window.addEventListener('resize', repositionOpenMultiSelectPanels);
    document.addEventListener('scroll', repositionOpenMultiSelectPanels, true);
}

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
        statuses: getSelectedMultiSelectValues('incidentStatusFilter'),
        types: getSelectedMultiSelectValues('incidentTypeFilter'),
        search: document.getElementById('incidentSearchInput')?.value.trim().toLowerCase() ?? ''
    };
}

function filterIncidents(incidents) {
    const filters = getIncidentFilters();

    return incidents.filter(incident => {
        if (filters.statuses.length > 0) {
            const incidentStatus = incident.isOpen ? 'open' : 'resolved';
            if (!filters.statuses.includes(incidentStatus)) {
                return false;
            }
        }

        if (filters.types.length > 0 && !filters.types.includes(incident.type)) {
            return false;
        }

        if (!filters.search) {
            return true;
        }

        const haystack = `${incident.displayName} ${incident.key} ${incident.type}`.toLowerCase();
        return haystack.includes(filters.search);
    });
}

async function addDnsRecordTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const host = document.getElementById('dnsRecordHostInput').value.trim();
    const recordType = document.getElementById('dnsRecordTypeInput').value;
    const expectedValue = document.getElementById('dnsRecordExpectedValueInput').value.trim();
    const containsText = document.getElementById('dnsRecordContainsTextInput').value.trim();

    if (!host || !recordType) {
        setActionStatus('L’hôte DNS et le type d’enregistrement sont obligatoires.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout du check DNS record ${recordType} ${host}...`);

    try {
        const expectedValueQuery = expectedValue ? `&expectedValue=${encodeURIComponent(expectedValue)}` : '';
        const containsTextQuery = containsText ? `&containsText=${encodeURIComponent(containsText)}` : '';
        const payload = await postAction(`/api/actions/add-dns-record?host=${encodeURIComponent(host)}&recordType=${encodeURIComponent(recordType)}${expectedValueQuery}${containsTextQuery}`);
        setActionStatus(payload.message);
        form.reset();
        document.getElementById('dnsRecordTypeInput').value = 'MX';
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du check DNS record : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function removeDnsRecordTarget(monitor, button) {
    if (!window.confirm(`Supprimer le check DNS record '${monitor.displayName}' du fichier YAML ?`)) {
        return;
    }

    button.disabled = true;
    setActionStatus(`Suppression du check DNS record ${monitor.displayName}...`);

    try {
        const expectedValueQuery = monitor.jsonValue ? `&expectedValue=${encodeURIComponent(monitor.jsonValue)}` : '';
        const containsTextQuery = monitor.failureReason ? `&containsText=${encodeURIComponent(monitor.failureReason)}` : '';
        const payload = await postAction(`/api/actions/remove-dns-record?host=${encodeURIComponent(monitor.hostName ?? '')}&recordType=${encodeURIComponent(monitor.recordType ?? monitor.displayName.split(' ')[0])}${expectedValueQuery}${containsTextQuery}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du check DNS record : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function addTlsTarget(event) {
    event.preventDefault();

    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const host = document.getElementById('tlsHostInput').value.trim();
    const port = Number(document.getElementById('tlsPortInput').value);
    const expectedHost = document.getElementById('tlsExpectedHostInput').value.trim();
    const warningDaysRaw = document.getElementById('tlsWarningDaysInput').value.trim();

    if (!host || !Number.isInteger(port) || port <= 0) {
        setActionStatus('L’hôte TLS et un port valide sont obligatoires.', true);
        return;
    }

    const warningDays = warningDaysRaw ? Number(warningDaysRaw) : null;
    if (warningDaysRaw && (!Number.isInteger(warningDays) || warningDays <= 0)) {
        setActionStatus('Le nombre de jours d’alerte TLS doit être un entier positif.', true);
        return;
    }

    button.disabled = true;
    setActionStatus(`Ajout du check TLS ${host}:${port}...`);

    try {
        const expectedHostQuery = expectedHost ? `&expectedHost=${encodeURIComponent(expectedHost)}` : '';
        const warningDaysQuery = warningDays === null ? '' : `&warningDays=${warningDays}`;
        const payload = await postAction(`/api/actions/add-tls?host=${encodeURIComponent(host)}&port=${port}${expectedHostQuery}${warningDaysQuery}`);
        setActionStatus(payload.message);
        form.reset();
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de l'ajout du check TLS : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function removeTlsTarget(key, button) {
    if (!window.confirm(`Supprimer le check TLS '${key}' du fichier YAML ?`)) {
        return;
    }

    const separatorIndex = key.lastIndexOf(':');
    if (separatorIndex <= 0) {
        setActionStatus(`Clé TLS invalide : ${key}`, true);
        return;
    }

    const host = key.slice(0, separatorIndex);
    const port = Number(key.slice(separatorIndex + 1));

    button.disabled = true;
    setActionStatus(`Suppression du check TLS ${key}...`);

    try {
        const payload = await postAction(`/api/actions/remove-tls?host=${encodeURIComponent(host)}&port=${port}`);
        setActionStatus(payload.message);
        await refresh();
    }
    catch (error) {
        setActionStatus(`Échec de la suppression du check TLS : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function generatePasswordHash() {
    const button = document.getElementById('passwordHashButton');
    const input = document.getElementById('passwordHashInput');
    const output = document.getElementById('passwordHashOutput');
    const password = input.value;
    if (!password) {
        setActionStatus('Le texte à hasher est obligatoire.', true);
        return;
    }

    button.disabled = true;
    setActionStatus('Génération du hash en cours...');

    try {
        const formData = new FormData();
        formData.append('password', password);

        const response = await fetch('/api/auth/hash-password', {
            method: 'POST',
            body: formData
        });

        const payload = await response.json();
        if (!response.ok || payload.success === false) {
            throw new Error(payload.message ?? `HTTP ${response.status}`);
        }

        output.value = payload.hash ?? '';
        setActionStatus(payload.message);
    }
    catch (error) {
        setActionStatus(`Échec de la génération du hash : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function copyPasswordHash() {
    const output = document.getElementById('passwordHashOutput');
    if (!output.value) {
        setActionStatus('Aucun hash à copier.', true);
        return;
    }

    try {
        await navigator.clipboard.writeText(output.value);
        setActionStatus('Hash copié dans le presse-papiers.');
    }
    catch (error) {
        setActionStatus(`Impossible de copier le hash : ${error.message}`, true);
    }
}

function rerenderMonitorLists() {
    renderMonitorList('pingMonitors', lastDashboardData.pingMonitors);
    renderMonitorList('tcpMonitors', lastDashboardData.tcpMonitors);
    renderMonitorList('httpMonitors', lastDashboardData.httpMonitors);
    renderMonitorList('dnsMonitors', lastDashboardData.dnsMonitors);
    renderMonitorList('tlsMonitors', lastDashboardData.tlsMonitors);
    renderMonitorList('dnsRecordMonitors', lastDashboardData.dnsRecordMonitors);
}

function initializeTabs() {
    const buttons = document.querySelectorAll('.tab-button');
    const panels = document.querySelectorAll('.tab-panel');

    buttons.forEach(button => {
        button.addEventListener('click', () => {
            const targetId = button.dataset.tabTarget;
            if (!targetId) {
                return;
            }

            buttons.forEach(item => item.classList.remove('active'));
            panels.forEach(panel => panel.classList.remove('active'));

            button.classList.add('active');
            document.getElementById(targetId)?.classList.add('active');
        });
    });
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
        statuses: getSelectedMultiSelectValues('monitorStatusFilter'),
        sources: getSelectedMultiSelectValues('monitorSourceFilter'),
        types: getSelectedMultiSelectValues('monitorTypeFilter'),
        sort: document.getElementById('monitorSortSelect')?.value ?? 'state',
        search: document.getElementById('monitorSearchInput')?.value.trim().toLowerCase() ?? ''
    };
}

function hasActiveMonitorFilters() {
    const filters = getMonitorFilters();
    return filters.statuses.length > 0
        || filters.sources.length > 0
        || filters.types.length > 0
        || filters.search.length > 0;
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
        if (filters.statuses.length > 0) {
            const monitorStatuses = [monitor.isDown ? 'down' : 'up'];
            if (monitor.snoozeUntil) {
                monitorStatuses.push('snoozed');
            }

            if (!filters.statuses.some(status => monitorStatuses.includes(status))) {
                return false;
            }
        }

        if (filters.sources.length > 0 && !filters.sources.includes(monitor.source)) {
            return false;
        }

        if (filters.types.length > 0 && !filters.types.includes(monitor.type)) {
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

async function generatePasswordHash(event) {
    event.preventDefault();

    const button = document.getElementById('passwordHashButton');
    const input = document.getElementById('passwordHashInput');
    const output = document.getElementById('passwordHashOutput');
    const password = input.value;
    if (!password) {
        setActionStatus('Le texte à hasher est obligatoire.', true);
        return;
    }

    button.disabled = true;
    setActionStatus('Génération du hash en cours...');

    try {
        const formData = new FormData();
        formData.append('password', password);

        const response = await fetch('/api/auth/hash-password', {
            method: 'POST',
            body: formData
        });

        const payload = await response.json();
        if (!response.ok || payload.success === false) {
            throw new Error(payload.message ?? `HTTP ${response.status}`);
        }

        output.value = payload.hash ?? '';
        setActionStatus(payload.message);
    }
    catch (error) {
        setActionStatus(`Échec de la génération du hash : ${error.message}`, true);
    }
    finally {
        button.disabled = false;
    }
}

async function copyPasswordHash() {
    const output = document.getElementById('passwordHashOutput');
    if (!output.value) {
        setActionStatus('Aucun hash à copier.', true);
        return;
    }

    try {
        await navigator.clipboard.writeText(output.value);
        setActionStatus('Hash copié dans le presse-papiers.');
    }
    catch (error) {
        setActionStatus(`Impossible de copier le hash : ${error.message}`, true);
    }
}

function renderMonitorList(containerId, monitors) {
    const container = document.getElementById(containerId);
    const panel = container.closest('.panel');
    container.innerHTML = '';

    const visibleMonitors = applyMonitorFiltersAndSort(monitors ?? []);
    const shouldHidePanel = visibleMonitors.length === 0 && hasActiveMonitorFilters();

    if (panel) {
        panel.hidden = shouldHidePanel;
    }

    if (shouldHidePanel) {
        return;
    }

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
        state.classList.add(monitor.isDown ? 'down' : monitor.isWarning ? 'warning' : 'up');
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

                if (monitor.type === 'TLS') {
                    removeTlsTarget(monitor.displayName, removeButton);
                    return;
                }

                if (monitor.type === 'DNS Record') {
                    removeDnsRecordTarget(monitor, removeButton);
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
            ...(monitor.type === 'DNS' && monitor.hostName ? [['Nom d’hôte', monitor.hostName]] : []),
            ...(monitor.type === 'TLS' && monitor.hostName ? [['Hostname attendu', monitor.hostName]] : []),
            ...(monitor.type === 'TLS' && monitor.certificateSubject ? [['Sujet certificat', monitor.certificateSubject]] : []),
            ...(monitor.type === 'TLS' && monitor.certificateIssuer ? [['Émetteur certificat', monitor.certificateIssuer]] : []),
            ...(monitor.type === 'TLS' && monitor.certificateNotAfter ? [['Expiration certificat', formatDate(monitor.certificateNotAfter)]] : []),
            ...(monitor.type === 'TLS' && monitor.daysRemaining !== null && monitor.daysRemaining !== undefined ? [['Jours restants', `${monitor.daysRemaining} jour(s)`]] : []),
            ...(monitor.type === 'HTTP' && monitor.httpStatusCode !== null && monitor.httpStatusCode !== undefined ? [['Code HTTP', String(monitor.httpStatusCode)]] : []),
            ...(monitor.type === 'HTTP' && monitor.failureReason ? [['Dernière raison d’échec', monitor.failureReason]] : []),
            ...(monitor.type === 'HTTP' && monitor.headerValue ? [['Valeur header', monitor.headerValue]] : []),
            ...(monitor.type === 'HTTP' && monitor.jsonValue ? [['Valeur JSON', monitor.jsonValue]] : []),
            ...(monitor.type === 'DNS Record' && monitor.hostName ? [['Hôte DNS', monitor.hostName]] : []),
            ...(monitor.type === 'DNS Record' && monitor.recordType ? [['Type record', monitor.recordType]] : []),
            ...(monitor.type === 'DNS Record' && monitor.jsonValue ? [['Valeur attendue', monitor.jsonValue]] : []),
            ...(monitor.type === 'DNS Record' && monitor.headerValue ? [['Valeur observée', monitor.headerValue]] : []),
            ...(monitor.type === 'DNS Record' && monitor.failureReason ? [['Dernière raison d’échec', monitor.failureReason]] : []),
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
            dnsMonitors: data.dnsMonitors ?? [],
            tlsMonitors: data.tlsMonitors ?? [],
            dnsRecordMonitors: data.dnsRecordMonitors ?? []
        };
        updateMonitorFilterOptions();
        rerenderMonitorLists();
        lastIncidents = data.recentIncidents ?? [];
        updateIncidentFilterOptions();
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
document.getElementById('tlsTargetForm').addEventListener('submit', addTlsTarget);
document.getElementById('dnsRecordTargetForm').addEventListener('submit', addDnsRecordTarget);
document.getElementById('refreshIntervalSelect').addEventListener('change', event => {
    localStorage.setItem(refreshIntervalStorageKey, event.currentTarget.value);
    applyRefreshIntervalPreference();
    updateRefreshIntervalDisplay();
    scheduleNextRefresh();
});
document.getElementById('incidentSearchInput').addEventListener('input', () => renderIncidentList('incidentHistory', lastIncidents));
document.getElementById('saveConfigButton').addEventListener('click', saveConfigEditor);
document.getElementById('reloadConfigButton').addEventListener('click', loadConfigEditor);
document.getElementById('configImportInput').addEventListener('change', importConfigFile);
document.getElementById('passwordHashButton').addEventListener('click', generatePasswordHash);
document.getElementById('passwordHashInput').addEventListener('keydown', event => {
    if (event.key !== 'Enter') {
        return;
    }

    event.preventDefault();
    generatePasswordHash();
});
document.getElementById('copyPasswordHashButton').addEventListener('click', copyPasswordHash);
document.getElementById('monitorSortSelect').addEventListener('change', rerenderMonitorLists);
document.getElementById('monitorSearchInput').addEventListener('input', rerenderMonitorLists);
initializeMultiSelectFilters();
initializeTabs();
refresh();
