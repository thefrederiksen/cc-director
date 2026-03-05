// CC Browser v2 - WebSocket Transport
// Manages WebSocket connections from native messaging hosts.
// Each connection maps to a browser instance (by connection name).

import { WebSocketServer } from 'ws';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_COMMAND_TIMEOUT_MS = 30000;

// ---------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------

export class Transport {
  constructor() {
    this._wss = null;
    this._connections = new Map(); // connectionName -> WebSocket
    this._pendingRequests = new Map(); // requestId -> { resolve, reject, timer }
    this._requestCounter = 0;
  }

  /**
   * Start WebSocket server on the given HTTP server.
   * @param {import('http').Server} httpServer
   */
  attach(httpServer) {
    this._wss = new WebSocketServer({ server: httpServer, path: '/ws' });

    this._wss.on('connection', (ws, req) => {
      const url = new URL(req.url, 'http://localhost');
      const connectionName = url.searchParams.get('connection') || 'default';

      console.log(`[transport] Connection established: ${connectionName}`);

      // Replace existing connection for this name
      const existing = this._connections.get(connectionName);
      if (existing) {
        console.log(`[transport] Replacing existing connection: ${connectionName}`);
        existing.close();
      }

      this._connections.set(connectionName, ws);

      ws.on('message', (data) => {
        this._handleMessage(connectionName, data);
      });

      ws.on('close', () => {
        console.log(`[transport] Connection closed: ${connectionName}`);
        if (this._connections.get(connectionName) === ws) {
          this._connections.delete(connectionName);
        }
      });

      ws.on('error', (err) => {
        console.error(`[transport] Connection error (${connectionName}): ${err.message}`);
      });
    });
  }

  /**
   * Send a command to a connection and wait for the response.
   * @param {string} connectionName
   * @param {string} command
   * @param {object} params
   * @param {number} [timeoutMs]
   * @returns {Promise<any>}
   */
  sendCommand(connectionName, command, params = {}, timeoutMs = DEFAULT_COMMAND_TIMEOUT_MS) {
    const ws = this._connections.get(connectionName);
    if (!ws || ws.readyState !== 1) {
      return Promise.reject(
        new Error(`Connection "${connectionName}" is not connected. Open it first.`)
      );
    }

    const id = `req-${++this._requestCounter}`;

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._pendingRequests.delete(id);
        reject(new Error(`Command "${command}" timed out after ${timeoutMs}ms`));
      }, timeoutMs);

      this._pendingRequests.set(id, { resolve, reject, timer });

      const msg = JSON.stringify({ id, command, params });
      ws.send(msg);
    });
  }

  /**
   * Check if a connection is active.
   * @param {string} connectionName
   * @returns {boolean}
   */
  isConnected(connectionName) {
    const ws = this._connections.get(connectionName);
    return ws !== undefined && ws.readyState === 1;
  }

  /**
   * List all active connections.
   * @returns {string[]}
   */
  listConnections() {
    const names = [];
    for (const [name, ws] of this._connections) {
      if (ws.readyState === 1) {
        names.push(name);
      }
    }
    return names;
  }

  /**
   * Close a specific connection.
   * @param {string} connectionName
   */
  closeConnection(connectionName) {
    const ws = this._connections.get(connectionName);
    if (ws) {
      ws.close();
      this._connections.delete(connectionName);
    }
  }

  /**
   * Shut down the transport.
   */
  close() {
    // Reject all pending requests
    for (const [id, pending] of this._pendingRequests) {
      clearTimeout(pending.timer);
      pending.reject(new Error('Transport shutting down'));
    }
    this._pendingRequests.clear();

    // Close all connections
    for (const [name, ws] of this._connections) {
      ws.close();
    }
    this._connections.clear();

    if (this._wss) {
      this._wss.close();
      this._wss = null;
    }
  }

  // ---------------------------------------------------------------------------
  // Internal
  // ---------------------------------------------------------------------------

  _handleMessage(connectionName, data) {
    let msg;
    try {
      msg = JSON.parse(data.toString());
    } catch (err) {
      console.error(`[transport] Invalid JSON from ${connectionName}: ${err.message}`);
      return;
    }

    if (!msg.id) {
      console.warn(`[transport] Message without id from ${connectionName}:`, msg);
      return;
    }

    const pending = this._pendingRequests.get(msg.id);
    if (!pending) {
      // Could be an event or stale response
      return;
    }

    clearTimeout(pending.timer);
    this._pendingRequests.delete(msg.id);

    if (msg.error) {
      pending.reject(new Error(msg.error));
    } else {
      pending.resolve(msg.result);
    }
  }
}
