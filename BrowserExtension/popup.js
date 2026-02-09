// V-Notch Bridge - Popup Script

const WS_PORT = 52741;

async function checkConnection() {
    const indicator = document.getElementById('connectionIndicator');
    const text = document.getElementById('connectionText');

    try {
        // Try to open a temporary WebSocket to check if V-Notch is running
        const ws = new WebSocket(`ws://localhost:${WS_PORT}`);

        ws.onopen = () => {
            indicator.className = 'indicator connected';
            text.textContent = 'Connected';
            ws.close();
        };

        ws.onerror = () => {
            indicator.className = 'indicator disconnected';
            text.textContent = 'Not Running';
        };
    } catch (e) {
        indicator.className = 'indicator disconnected';
        text.textContent = 'Not Running';
    }
}

async function countYouTubeTabs() {
    const tabs = await chrome.tabs.query({ url: '*://*.youtube.com/*' });
    const videoTabs = tabs.filter(tab =>
        tab.url.includes('watch?v=') ||
        tab.url.includes('youtube.com/shorts/')
    );
    document.getElementById('tabCount').textContent = videoTabs.length;
}

// Initialize
document.addEventListener('DOMContentLoaded', () => {
    checkConnection();
    countYouTubeTabs();

    // Refresh periodically
    setInterval(checkConnection, 5000);
});
