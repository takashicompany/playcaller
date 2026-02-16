import net from 'net';
import { EventEmitter } from 'events';

const UNITY_HOST = '127.0.0.1';
const UNITY_PORT = 6500;
const COMMAND_TIMEOUT_MS = 30000;
const RECONNECT_DELAY_MS = 2000;
const MAX_RECONNECT_DELAY_MS = 30000;

interface PendingCommand {
  resolve: (data: unknown) => void;
  reject: (error: Error) => void;
  timeout: ReturnType<typeof setTimeout>;
}

/**
 * Manages TCP connection to Unity Editor with big-endian 4-byte length-prefix framing.
 */
export class UnityConnection extends EventEmitter {
  private socket: net.Socket | null = null;
  private connected = false;
  private commandId = 0;
  private pendingCommands = new Map<string, PendingCommand>();
  private messageBuffer = Buffer.alloc(0);
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private isDisconnecting = false;
  private connectPromise: Promise<void> | null = null;

  /** Last screenshot dimensions for coordinate conversion in tap/drag/flick */
  lastScreenshotWidth = 0;
  lastScreenshotHeight = 0;

  /** Actual Game View screen dimensions (from Screen.width/height at screenshot time) */
  lastScreenWidth = 0;
  lastScreenHeight = 0;

  /**
   * Connect to Unity Editor's TCP server.
   * This is called lazily on first command, not at MCP startup.
   */
  async connect(): Promise<void> {
    if (this.connectPromise) return this.connectPromise;
    if (this.connected) return;

    this.connectPromise = new Promise<void>((resolve, reject) => {
      console.error(`[playcaller] Connecting to Unity at ${UNITY_HOST}:${UNITY_PORT}...`);

      this.socket = new net.Socket();
      let resolved = false;
      let connectionTimeout: ReturnType<typeof setTimeout> | null = null;

      const settle = (fn: Function, value?: unknown) => {
        if (resolved) return;
        resolved = true;
        if (connectionTimeout) {
          clearTimeout(connectionTimeout);
          connectionTimeout = null;
        }
        this.connectPromise = null;
        fn(value);
      };

      this.socket.on('connect', () => {
        console.error(`[playcaller] Connected to Unity at ${UNITY_HOST}:${UNITY_PORT}`);
        this.connected = true;
        this.reconnectAttempts = 0;
        this.emit('connected');
        settle(resolve);
      });

      this.socket.on('data', (data: Buffer) => {
        this.handleData(data);
      });

      this.socket.on('error', (error: Error) => {
        console.error(`[playcaller] Socket error: ${error.message}`);
        if (!this.connected && !resolved) {
          this.isDisconnecting = true;
          this.socket?.destroy();
          this.isDisconnecting = false;
          settle(reject, new Error(`Unity connection failed: ${error.message}`));
        }
      });

      this.socket.on('close', () => {
        if (this.isDisconnecting) return;
        console.error('[playcaller] Disconnected from Unity');
        this.connected = false;
        this.socket = null;
        this.messageBuffer = Buffer.alloc(0);

        // Reject all pending commands
        for (const [, pending] of this.pendingCommands) {
          clearTimeout(pending.timeout);
          pending.reject(new Error('Connection closed'));
        }
        this.pendingCommands.clear();

        this.emit('disconnected');

        if (!this.isDisconnecting) {
          this.scheduleReconnect();
        }
      });

      this.socket.connect(UNITY_PORT, UNITY_HOST);

      connectionTimeout = setTimeout(() => {
        if (!this.connected && !resolved && this.socket) {
          this.socket.removeAllListeners();
          this.socket.destroy();
          this.connectPromise = null;
          settle(reject, new Error('Unity connection timeout'));
        }
      }, 10000);
    });

    return this.connectPromise;
  }

  disconnect(): void {
    this.isDisconnecting = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.socket) {
      this.socket.removeAllListeners();
      this.socket.destroy();
      this.socket = null;
    }
    this.connected = false;
    this.isDisconnecting = false;
  }

  isConnected(): boolean {
    return this.connected;
  }

  /**
   * Send a command to Unity and wait for response.
   */
  async sendCommand(type: string, params: Record<string, unknown> = {}): Promise<unknown> {
    if (!this.connected) {
      await this.connect();
    }

    const id = String(++this.commandId);
    const command = { id, type, params };
    const json = JSON.stringify(command);
    const messageBuffer = Buffer.from(json, 'utf8');
    const lengthBuffer = Buffer.allocUnsafe(4);
    lengthBuffer.writeInt32BE(messageBuffer.length, 0);
    const framedMessage = Buffer.concat([lengthBuffer, messageBuffer]);

    return new Promise<unknown>((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pendingCommands.delete(id);
        reject(new Error(`Command ${type} timed out after ${COMMAND_TIMEOUT_MS}ms`));
      }, COMMAND_TIMEOUT_MS);

      this.pendingCommands.set(id, { resolve, reject, timeout });

      this.socket!.write(framedMessage, (error) => {
        if (error) {
          clearTimeout(timeout);
          this.pendingCommands.delete(id);
          reject(error);
        }
      });
    });
  }

  private handleData(data: Buffer): void {
    this.messageBuffer = Buffer.concat([this.messageBuffer, data]);

    while (this.messageBuffer.length >= 4) {
      const messageLength = this.messageBuffer.readInt32BE(0);

      if (messageLength < 0 || messageLength > 1024 * 1024) {
        console.error(`[playcaller] Invalid message length: ${messageLength}`);
        this.messageBuffer = Buffer.alloc(0);
        break;
      }

      if (this.messageBuffer.length >= 4 + messageLength) {
        const messageData = this.messageBuffer.subarray(4, 4 + messageLength);
        this.messageBuffer = this.messageBuffer.subarray(4 + messageLength);

        try {
          const message = messageData.toString('utf8');
          const response = JSON.parse(message);
          this.processResponse(response);
        } catch (error) {
          console.error(`[playcaller] Failed to parse response: ${(error as Error).message}`);
        }
      } else {
        break;
      }
    }
  }

  private processResponse(response: { id?: string; status?: string; result?: unknown; error?: string; code?: string }): void {
    const id = response.id != null ? String(response.id) : null;

    // Try to match by ID, or fallback to first pending command
    const targetId = (id && this.pendingCommands.has(id))
      ? id
      : (this.pendingCommands.size > 0 ? this.pendingCommands.keys().next().value : null);

    if (targetId) {
      const pending = this.pendingCommands.get(targetId)!;
      this.pendingCommands.delete(targetId);
      clearTimeout(pending.timeout);

      if (response.status === 'success') {
        pending.resolve(response.result ?? {});
      } else if (response.status === 'error') {
        const err = new Error(response.error || 'Command failed');
        (err as any).code = response.code;
        pending.reject(err);
      } else {
        pending.resolve(response);
      }
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;

    const delay = Math.min(
      RECONNECT_DELAY_MS * Math.pow(2, this.reconnectAttempts),
      MAX_RECONNECT_DELAY_MS
    );

    console.error(`[playcaller] Scheduling reconnection in ${delay}ms (attempt ${this.reconnectAttempts + 1})`);

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.reconnectAttempts++;
      this.connect().catch((error) => {
        console.error(`[playcaller] Reconnection failed: ${error.message}`);
      });
    }, delay);
  }
}
