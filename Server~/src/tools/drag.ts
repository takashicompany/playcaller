import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const dragParams = {
  fromX: z.number(),
  fromY: z.number(),
  toX: z.number(),
  toY: z.number(),
  durationMs: z.number().optional(),
  steps: z.number().optional(),
};

export function registerDragTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_drag',
    'Drag from one screen coordinate to another. Coordinates use top-left origin, Y-down (matching screenshot). fromX/fromY: start, toX/toY: end. durationMs: drag duration (default 300, max 10000). steps: intermediate points (default 10, 2-100).',
    dragParams,
    async ({ fromX, fromY, toX, toY, durationMs, steps }) => {
      try {
        const result = await unity.sendCommand('drag', {
          fromX,
          fromY,
          toX,
          toY,
          durationMs: durationMs ?? 300,
          steps: steps ?? 10,
          screenshotWidth: unity.lastScreenshotWidth,
          screenshotHeight: unity.lastScreenshotHeight,
          screenWidth: unity.lastScreenWidth,
          screenHeight: unity.lastScreenHeight,
        }) as { dragged: boolean; targetName: string | null; steps: number };

        const status = result.dragged
          ? `Dragged "${result.targetName}" from (${fromX},${fromY}) to (${toX},${toY}) in ${result.steps} steps`
          : `Drag from (${fromX},${fromY}) to (${toX},${toY}) - no target hit`;

        return {
          content: [{ type: 'text' as const, text: status }],
        };
      } catch (error) {
        return {
          content: [{ type: 'text' as const, text: `Drag failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
