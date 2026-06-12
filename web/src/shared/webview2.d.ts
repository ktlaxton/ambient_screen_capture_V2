// Ambient typing for the WebView2 script bridge (window.chrome.webview).
export interface WebView2MessageEvent {
  data: unknown;
}

export interface WebView2Bridge {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: WebView2MessageEvent) => void): void;
  removeEventListener(type: 'message', listener: (event: WebView2MessageEvent) => void): void;
}

declare global {
  interface Window {
    chrome?: {
      webview?: WebView2Bridge;
    };
  }
}
