/**
 * CC Director Dashboard JavaScript
 * Handles WebSocket connection and utility functions
 */

// WebSocket connection
let ws = null;
let wsReconnectAttempts = 0;
const MAX_RECONNECT_ATTEMPTS = 10;
const RECONNECT_DELAY_MS = 3000;

/**
 * Initialize WebSocket connection
 */
function initWebSocket() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${window.location.host}/ws`;

    ws = new WebSocket(wsUrl);

    ws.onopen = function() {
        console.log('WebSocket connected');
        wsReconnectAttempts = 0;
        updateConnectionStatus('connected');
    };

    ws.onclose = function() {
        console.log('WebSocket disconnected');
        updateConnectionStatus('disconnected');
        attemptReconnect();
    };

    ws.onerror = function(error) {
        console.error('WebSocket error:', error);
        updateConnectionStatus('error');
    };

    ws.onmessage = function(event) {
        try {
            const message = JSON.parse(event.data);
            handleWebSocketMessage(message);
        } catch (e) {
            console.error('Failed to parse WebSocket message:', e);
        }
    };
}

/**
 * Attempt to reconnect WebSocket
 */
function attemptReconnect() {
    if (wsReconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
        console.log('Max reconnect attempts reached');
        updateConnectionStatus('failed');
        return;
    }

    wsReconnectAttempts++;
    console.log(`Attempting reconnect ${wsReconnectAttempts}/${MAX_RECONNECT_ATTEMPTS}...`);
    updateConnectionStatus('reconnecting');

    setTimeout(function() {
        initWebSocket();
    }, RECONNECT_DELAY_MS);
}

/**
 * Update the connection status indicator
 */
function updateConnectionStatus(status) {
    const statusEl = document.getElementById('connection-status');
    if (!statusEl) return;

    const statusConfig = {
        connected: { text: 'Connected', class: 'bg-green-100 text-green-800' },
        disconnected: { text: 'Disconnected', class: 'bg-gray-100 text-gray-800' },
        reconnecting: { text: 'Reconnecting...', class: 'bg-yellow-100 text-yellow-800' },
        error: { text: 'Error', class: 'bg-red-100 text-red-800' },
        failed: { text: 'Connection Failed', class: 'bg-red-100 text-red-800' },
    };

    const config = statusConfig[status] || statusConfig.disconnected;
    statusEl.textContent = config.text;
    statusEl.className = `inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${config.class}`;
}

/**
 * Handle incoming WebSocket messages
 */
function handleWebSocketMessage(message) {
    console.log('WebSocket message:', message);

    switch (message.type) {
        case 'job_started':
            showNotification(`Job "${message.job_name}" started`, 'info');
            refreshCurrentPage();
            break;

        case 'job_completed':
            showNotification(`Job "${message.job_name}" completed`, 'success');
            refreshCurrentPage();
            break;

        case 'job_failed':
            showNotification(`Job "${message.job_name}" failed`, 'error');
            refreshCurrentPage();
            break;

        case 'job_timeout':
            showNotification(`Job "${message.job_name}" timed out`, 'warning');
            refreshCurrentPage();
            break;

        case 'heartbeat':
            // Keep connection alive, no action needed
            break;

        default:
            console.log('Unknown message type:', message.type);
    }
}

/**
 * Refresh data on the current page
 */
function refreshCurrentPage() {
    // Call page-specific refresh functions if they exist
    if (typeof loadDashboardData === 'function') {
        loadDashboardData();
    }
    if (typeof loadJobs === 'function') {
        loadJobs();
    }
    if (typeof loadRuns === 'function') {
        loadRuns();
    }
    if (typeof loadJob === 'function') {
        loadJob();
    }
    if (typeof loadRun === 'function') {
        loadRun();
    }
}

/**
 * Show a notification toast
 */
function showNotification(message, type) {
    // Simple alert for now - could be enhanced with a toast library
    console.log(`[${type.toUpperCase()}] ${message}`);
}

/**
 * Format a duration in seconds to a human-readable string
 */
function formatDuration(seconds) {
    if (seconds === null || seconds === undefined) {
        return '-';
    }

    if (seconds < 1) {
        return '<1s';
    }

    if (seconds < 60) {
        return Math.round(seconds) + 's';
    }

    if (seconds < 3600) {
        const minutes = Math.floor(seconds / 60);
        const secs = Math.round(seconds % 60);
        return `${minutes}m ${secs}s`;
    }

    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    return `${hours}h ${minutes}m`;
}

/**
 * Format a timestamp to a relative or absolute time string
 */
function formatTime(timestamp) {
    if (!timestamp) {
        return '-';
    }

    const date = new Date(timestamp);
    const now = new Date();
    const diffMs = now - date;
    const diffSec = Math.floor(diffMs / 1000);
    const diffMin = Math.floor(diffSec / 60);
    const diffHour = Math.floor(diffMin / 60);
    const diffDay = Math.floor(diffHour / 24);

    // Future time
    if (diffMs < 0) {
        const futureSec = Math.abs(diffSec);
        const futureMin = Math.floor(futureSec / 60);
        const futureHour = Math.floor(futureMin / 60);

        if (futureMin < 1) {
            return 'in <1m';
        }
        if (futureMin < 60) {
            return `in ${futureMin}m`;
        }
        if (futureHour < 24) {
            return `in ${futureHour}h`;
        }
        return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    // Past time
    if (diffSec < 60) {
        return 'just now';
    }
    if (diffMin < 60) {
        return `${diffMin}m ago`;
    }
    if (diffHour < 24) {
        return `${diffHour}h ago`;
    }
    if (diffDay < 7) {
        return `${diffDay}d ago`;
    }

    return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

/**
 * Send a ping to keep the WebSocket connection alive
 */
function sendPing() {
    if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'ping' }));
    }
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', function() {
    initWebSocket();

    // Send ping every 25 seconds to keep connection alive
    setInterval(sendPing, 25000);
});
