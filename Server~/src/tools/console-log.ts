import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { UnityConnection } from '../unity-connection.js';

const consoleLogParams = {
  count: z.number().optional(),
  level: z.enum(['all', 'log', 'warning', 'error']).optional(),
  clear: z.boolean().optional(),
};

export function registerConsoleLogTool(server: McpServer, unity: UnityConnection): void {
  server.tool(
    'playcaller_console_log',
    'Get Unity Console log messages. count: max entries (default 50, max 200). level: filter (all/log/warning/error). clear: clear after retrieval (default false).',
    consoleLogParams,
    async ({ count, level, clear }) => {
      try {
        const result = await unity.sendCommand('console_log', {
          count: count ?? 50,
          level: level ?? 'all',
          clear: clear ?? false,
        }) as { logs: Array<{ level: string; message: string; timestamp: string }>; totalCount: number };

        let text = `Console Logs (${result.logs.length}/${result.totalCount}):\n`;
        for (const log of result.logs) {
          const prefix = log.level === 'error' ? '[ERR]' :
                         log.level === 'warning' ? '[WRN]' : '[LOG]';
          text += `${prefix} ${log.message}\n`;
        }

        return {
          content: [{ type: 'text' as const, text }],
        };
      } catch (error) {
        return {
          content: [{ type: 'text' as const, text: `Console log failed: ${(error as Error).message}` }],
          isError: true,
        };
      }
    }
  );
}
