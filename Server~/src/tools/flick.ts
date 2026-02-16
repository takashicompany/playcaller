import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const flickParams = {
  fromX: z.number(),
  fromY: z.number(),
  dx: z.number(),
  dy: z.number(),
  durationMs: z.number().optional(),
};

export function registerFlickTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_flick',
    'Flick (quick swipe) from a coordinate in a direction. Coordinates use top-left origin, Y-down. fromX/fromY: start point. dx: horizontal displacement (positive=right). dy: vertical displacement (positive=down). durationMs: flick duration (default 150, max 5000).',
    flickParams,
    async ({ fromX, fromY, dx, dy, durationMs }) => {
      try {
        const result = await unity.sendCommand('flick', {
          fromX,
          fromY,
          dx,
          dy,
          durationMs: durationMs ?? 150,
          screenshotWidth: unity.lastScreenshotWidth,
          screenshotHeight: unity.lastScreenshotHeight,
          screenWidth: unity.lastScreenWidth,
          screenHeight: unity.lastScreenHeight,
        }) as { dragged: boolean; targetName: string | null };

        const status = result.dragged
          ? `Flicked "${result.targetName}" from (${fromX},${fromY}) by (${dx},${dy})`
          : `Flick from (${fromX},${fromY}) by (${dx},${dy}) - no target hit`;

        return {
          content: [{ type: 'text' as const, text: status }],
        };
      } catch (error) {
        return {
          content: [{ type: 'text' as const, text: `Flick failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
