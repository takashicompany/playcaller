import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const tapParams = {
  x: z.number(),
  y: z.number(),
  holdDurationMs: z.number().optional(),
};

export function registerTapTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_tap',
    'Tap/click at a specific screen coordinate. Coordinates use top-left origin with Y-axis pointing down (matching screenshot image coordinates). x: pixels from left, y: pixels from top, holdDurationMs: hold duration before releasing (default 0, max 10000).',
    tapParams,
    async ({ x, y, holdDurationMs }) => {
      try {
        const result = await unity.sendCommand('tap', {
          x,
          y,
          holdDurationMs: holdDurationMs ?? 0,
          screenshotWidth: unity.lastScreenshotWidth,
          screenshotHeight: unity.lastScreenshotHeight,
          screenWidth: unity.lastScreenWidth,
          screenHeight: unity.lastScreenHeight,
        }) as { tapped: boolean; targetName: string | null };

        const status = result.tapped
          ? `Tapped on "${result.targetName}" at (${x}, ${y})`
          : `Tap at (${x}, ${y}) - no target hit`;

        return {
          content: [{ type: 'text' as const, text: status }],
        };
      } catch (error) {
        return {
          content: [{ type: 'text' as const, text: `Tap failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
