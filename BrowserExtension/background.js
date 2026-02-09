// V-Notch Bridge - Background Service Worker
// Manages persistent connection and tab communication

const WS_PORT = 52741;
let reconnectAttempts = 0;
const MAX_RECONNECT_ATTEMPTS = 10;

// Keep track of active YouTube tabs
const activeTabs = new Map();

// Listen for messages from content scripts
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message.type === 'youtube_status' && sender.tab) {
        activeTabs.set(sender.tab.id, {
            ...message,
            tabId: sender.tab.id,
            lastUpdate: Date.now()
        });
    }
    return true;
});

// Clean up when tabs are closed
chrome.tabs.onRemoved.addListener((tabId) => {
    activeTabs.delete(tabId);
});

// Badge to show connection status
function updateBadge(connected) {
    chrome.action.setBadgeText({ text: connected ? 'ON' : '' });
    chrome.action.setBadgeBackgroundColor({ color: connected ? '#4CAF50' : '#F44336' });
}

// Initialize
updateBadge(false);

console.log('[V-Notch Bridge] Background service worker started');
