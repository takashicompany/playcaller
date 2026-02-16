import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const playmodeParams = {
  action: z.enum(['play', 'pause', 'stop', 'get_state']),
};

/**
 * Wait for Unity to reconnect after domain reload (play/stop transitions).
 * Returns true if reconnected, false if timed out.
 */
async function waitForReconnect(unity: UnityConnection, timeoutMs: number): Promise<boolean> {
  const start = Date.now();
  const pollInterval = 500;

  // First, wait a bit for the disconnect to happen
  await new Promise(r => setTimeout(r, 1000));

  while (Date.now() - start < timeoutMs) {
    if (unity.isConnected()) {
      // Verify connection is alive with a ping
      try {
        await unity.sendCommand('ping', {});
        return true;
      } catch {
        // Connection was stale, wait and retry
      }
    } else {
      // Try to reconnect
      try {
        await unity.connect();
        // Verify with ping
        try {
          await unity.sendCommand('ping', {});
          return true;
        } catch {
          // Connected but ping failed, wait
        }
      } catch {
        // Connection failed, will retry
      }
    }
    await new Promise(r => setTimeout(r, pollInterval));
  }
  return false;
}

export function registerPlaymodeTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_playmode',
    'Control Unity Play Mode. action: "play" to start, "pause" to toggle pause, "stop" to stop, "get_state" to check current state. Play/stop will wait for Unity domain reload to complete before returning.',
    playmodeParams,
    async ({ action }) => {
      try {
        const result = await unity.sendCommand('playmode', { action }) as {
          isPlaying: boolean;
          isPaused: boolean;
          message: string;
        };

        // Play/Stop trigger domain reload which disconnects TCP.
        // Wait for Unity to come back before returning to the caller.
        if (action === 'play' || action === 'stop') {
          const reconnected = await waitForReconnect(unity, 15000);
          if (!reconnected) {
            return {
              content: [{
                type: 'text' as const,
                text: JSON.stringify(result, null, 2) + '\n\nWarning: Unity domain reload completed but TCP reconnection timed out. Subsequent commands may fail. Try playcaller_wait with ms=2000 before next command.',
              }],
            };
          }
        }

        return {
          content: [{
            type: 'text' as const,
            text: JSON.stringify(result, null, 2),
          }],
        };
      } catch (error) {
        // play/stop commands may get "Connection closed" error due to domain reload
        if (action === 'play' || action === 'stop') {
          console.error(`[playcaller] ${action} command got error (expected during domain reload): ${(error as Error).message}`);
          const reconnected = await waitForReconnect(unity, 15000);
          if (reconnected) {
            return {
              content: [{
                type: 'text' as const,
                text: `Play Mode ${action === 'play' ? 'started' : 'stopped'} (reconnected after domain reload)`,
              }],
            };
          }
          return {
            content: [{
              type: 'text' as const,
              text: `Play Mode ${action} sent but reconnection failed after domain reload. Unity may still be reloading.`,
            }],
            isError: true,
          };
        }
        return {
          content: [{ type: 'text' as const, text: `PlayMode command failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
