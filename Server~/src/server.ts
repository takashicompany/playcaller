import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { UnityConnection } from './unity-connection.js';
import { registerTapTool } from './tools/tap.js';
import { registerDragTool } from './tools/drag.js';
import { registerFlickTool } from './tools/flick.js';
import { registerScreenshotTool } from './tools/screenshot.js';
import { registerPlaymodeTool } from './tools/playmode.js';
import { registerConsoleLogTool } from './tools/console-log.js';
import { registerWaitTool } from './tools/wait.js';

export function createServer(): { server: McpServer; unity: UnityConnection } {
  const server = new McpServer({
    name: 'playcaller',
    version: '0.1.0',
  });

  const unity = new UnityConnection();

  // Register all tools
  registerTapTool(server, unity);
  registerDragTool(server, unity);
  registerFlickTool(server, unity);
  registerScreenshotTool(server, unity);
  registerPlaymodeTool(server, unity);
  registerConsoleLogTool(server, unity);
  registerWaitTool(server, unity);

  return { server, unity };
}
