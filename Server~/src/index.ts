import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { createServer } from './server.js';

async function main(): Promise<void> {
  const { server, unity } = createServer();

  // Connect stdio transport first (MCP 10-second timeout)
  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error('[playcaller] MCP server started on stdio');

  // Connect to Unity in background (deferred initialization)
  unity.connect().then(() => {
    console.error('[playcaller] Unity connection established');
  }).catch((error) => {
    console.error(`[playcaller] Initial Unity connection failed (will retry): ${error.message}`);
  });

  // Handle process signals
  process.on('SIGINT', () => {
    console.error('[playcaller] Shutting down...');
    unity.disconnect();
    process.exit(0);
  });

  process.on('SIGTERM', () => {
    console.error('[playcaller] Shutting down...');
    unity.disconnect();
    process.exit(0);
  });
}

main().catch((error) => {
  console.error(`[playcaller] Fatal error: ${error}`);
  process.exit(1);
});
