// V-Notch Bridge - YouTube Content Script
// Injects into YouTube pages to capture video information and provide controls

(function () {
    'use strict';

    const DEBUG = false;
    const UPDATE_INTERVAL = 500; // ms

    let currentVideo = null;
    let updateTimer = null;
    let lastState = null;
    let ws = null;
    let wsReconnectTimer = null;
    const WS_PORT = 52741;

    function log(...args) {
        if (DEBUG) console.log('[V-Notch Bridge]', ...args);
    }

    // WebSocket connection to V-Notch
    function connectWebSocket() {
        if (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN)) {
            return;
        }

        try {
            ws = new WebSocket(`ws://localhost:${WS_PORT}`);

            ws.onopen = () => {
                log('Connected to V-Notch');
                sendStatusUpdate();
            };

            ws.onmessage = (event) => {
                handleCommand(JSON.parse(event.data));
            };

            ws.onclose = () => {
                log('Disconnected from V-Notch');
                ws = null;
                // Reconnect after 3 seconds
                if (!wsReconnectTimer) {
                    wsReconnectTimer = setTimeout(() => {
                        wsReconnectTimer = null;
                        connectWebSocket();
                    }, 3000);
                }
            };

            ws.onerror = () => {
                log('WebSocket error');
            };
        } catch (e) {
            log('WebSocket connection failed:', e);
        }
    }

    function sendToNotch(data) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(data));
        }
    }

    // Find YouTube video element
    function findVideoElement() {
        // Main video player
        let video = document.querySelector('video.html5-main-video');
        if (!video) {
            video = document.querySelector('video');
        }
        return video;
    }

    // Get video information
    function getVideoInfo() {
        const video = findVideoElement();
        if (!video) return null;

        const isPlaying = !video.paused && !video.ended;
        const currentTime = video.currentTime || 0;
        const duration = video.duration || 0;
        const progress = duration > 0 ? currentTime / duration : 0;

        // Get video title
        let title = '';
        const titleElement = document.querySelector('h1.ytd-video-primary-info-renderer, h1.ytd-watch-metadata, #title h1 yt-formatted-string');
        if (titleElement) {
            title = titleElement.textContent?.trim() || '';
        }
        if (!title) {
            // Fallback to document title
            const docTitle = document.title;
            if (docTitle.endsWith(' - YouTube')) {
                title = docTitle.slice(0, -10);
            } else {
                title = docTitle;
            }
        }

        // Get channel name
        let channel = '';
        const channelElement = document.querySelector('#channel-name a, ytd-channel-name a, #owner-name a');
        if (channelElement) {
            channel = channelElement.textContent?.trim() || '';
        }

        // Get thumbnail URL
        let thumbnailUrl = '';
        const videoId = getVideoId();
        if (videoId) {
            thumbnailUrl = `https://i.ytimg.com/vi/${videoId}/maxresdefault.jpg`;
        }

        return {
            type: 'youtube_status',
            source: 'YouTube',
            title: title,
            artist: channel,
            isPlaying: isPlaying,
            currentTime: currentTime,
            duration: duration,
            progress: progress,
            thumbnailUrl: thumbnailUrl,
            videoId: videoId,
            volume: video.volume,
            muted: video.muted
        };
    }

    // Get YouTube video ID from URL
    function getVideoId() {
        const urlParams = new URLSearchParams(window.location.search);
        return urlParams.get('v');
    }

    // Send status update to V-Notch
    function sendStatusUpdate() {
        const info = getVideoInfo();
        if (info) {
            // Only send if state changed
            const stateStr = JSON.stringify(info);
            if (stateStr !== lastState) {
                lastState = stateStr;
                log('Sending update:', info);
                sendToNotch(info);
            }
        }
    }

    // Handle commands from V-Notch
    function handleCommand(command) {
        log('Received command:', command);
        const video = findVideoElement();
        if (!video) return;

        switch (command.action) {
            case 'play':
                video.play();
                break;

            case 'pause':
                video.pause();
                break;

            case 'toggle':
                if (video.paused) {
                    video.play();
                } else {
                    video.pause();
                }
                break;

            case 'seek':
                if (typeof command.time === 'number') {
                    video.currentTime = command.time;
                } else if (typeof command.progress === 'number') {
                    video.currentTime = video.duration * command.progress;
                }
                break;

            case 'seekRelative':
                video.currentTime += command.offset || 0;
                break;

            case 'setVolume':
                video.volume = Math.max(0, Math.min(1, command.volume || 0));
                break;

            case 'mute':
                video.muted = true;
                break;

            case 'unmute':
                video.muted = false;
                break;

            case 'toggleMute':
                video.muted = !video.muted;
                break;

            case 'next':
                clickButton('.ytp-next-button');
                break;

            case 'previous':
                // YouTube doesn't have a previous button, so seek to start
                video.currentTime = 0;
                break;

            case 'getStatus':
                sendStatusUpdate();
                break;
        }

        // Send updated status after command
        setTimeout(sendStatusUpdate, 100);
    }

    // Helper to click YouTube player buttons
    function clickButton(selector) {
        const btn = document.querySelector(selector);
        if (btn) {
            btn.click();
            return true;
        }
        return false;
    }

    // Start monitoring
    function startMonitoring() {
        if (updateTimer) return;

        updateTimer = setInterval(() => {
            const video = findVideoElement();
            if (video !== currentVideo) {
                currentVideo = video;
                log('Video element changed:', !!video);
            }
            sendStatusUpdate();
        }, UPDATE_INTERVAL);

        log('Monitoring started');
    }

    // Stop monitoring
    function stopMonitoring() {
        if (updateTimer) {
            clearInterval(updateTimer);
            updateTimer = null;
        }
        log('Monitoring stopped');
    }

    // Initialize
    function init() {
        log('Initializing on', window.location.href);

        // Connect to V-Notch
        connectWebSocket();

        // Start monitoring for video
        startMonitoring();

        // Watch for page navigation (YouTube is SPA)
        const observer = new MutationObserver(() => {
            const video = findVideoElement();
            if (video !== currentVideo) {
                currentVideo = video;
                sendStatusUpdate();
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        // Listen for video events
        document.addEventListener('play', () => sendStatusUpdate(), true);
        document.addEventListener('pause', () => sendStatusUpdate(), true);
        document.addEventListener('seeked', () => sendStatusUpdate(), true);
        document.addEventListener('volumechange', () => sendStatusUpdate(), true);
        document.addEventListener('ended', () => sendStatusUpdate(), true);
    }

    // Wait for DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Cleanup on unload
    window.addEventListener('beforeunload', () => {
        stopMonitoring();
        if (ws) {
            sendToNotch({ type: 'youtube_closed' });
            ws.close();
        }
    });

})();
