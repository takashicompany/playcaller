import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const waitParams = {
  ms: z.number().optional(),
  frames: z.number().optional(),
};

export function registerWaitTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_wait',
    'Wait for a specified duration. ms: milliseconds to wait (max 30000). frames: Unity frame count to wait (max 600). Specify either ms or frames.',
    waitParams,
    async ({ ms, frames }) => {
      try {
        if (ms == null && frames == null) {
          return {
            content: [{ type: 'text' as const, text: 'Either ms or frames must be specified' }],
            isError: true,
          };
        }

        const result = await unity.sendCommand('wait', {
          ms: ms ?? 0,
          frames: frames ?? 0,
        }) as { waited: string };

        return {
          content: [{ type: 'text' as const, text: `Wait completed: ${result.waited}` }],
        };
      } catch (error) {
        return {
          content: [{ type: 'text' as const, text: `Wait failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
