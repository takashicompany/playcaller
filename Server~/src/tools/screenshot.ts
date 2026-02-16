import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const screenshotParams = {
  width: z.number().optional(),
  height: z.number().optional(),
};

export function registerScreenshotTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_screenshot',
    'Capture a screenshot of the Unity Game View. Returns the image as base64-encoded PNG. width/height: optional resolution in pixels (default: Game View size).',
    screenshotParams,
    async ({ width, height }) => {
      try {
        const result = await unity.sendCommand('screenshot', {
          width: width ?? 0,
          height: height ?? 0,
        }) as { base64: string; width: number; height: number; screenWidth?: number; screenHeight?: number };

        // Store screenshot dimensions for coordinate conversion in tap/drag/flick
        unity.lastScreenshotWidth = result.width;
        unity.lastScreenshotHeight = result.height;
        // Store actual screen dimensions for proper scaling when screenshot is resized
        unity.lastScreenWidth = result.screenWidth ?? result.width;
        unity.lastScreenHeight = result.screenHeight ?? result.height;

        return {
          content: [
            {
              type: 'image' as const,
              data: result.base64,
              mimeType: 'image/png' as const,
            },
            {
              type: 'text' as const,
              text: `Screenshot captured: ${result.width}x${result.height} (screen: ${result.screenWidth ?? result.width}x${result.screenHeight ?? result.height}). Input coordinates for tap/drag/flick should use the screenshot image coordinate system (${result.width}x${result.height}, top-left origin, Y-down).`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [{ type: 'text' as const, text: `Screenshot failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
