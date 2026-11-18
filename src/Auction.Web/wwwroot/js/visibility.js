// Calls back into .NET whenever the tab becomes visible again, so the open view can re-fetch.
export function onVisible(dotnet) {
    const handler = () => {
        if (document.visibilityState === 'visible') {
            dotnet.invokeMethodAsync('OnVisibleAsync');
        }
    };
    document.addEventListener('visibilitychange', handler);
    return { dispose: () => document.removeEventListener('visibilitychange', handler) };
}
