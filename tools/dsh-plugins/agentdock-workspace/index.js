/**
 * Host half: register Agent Dock's launch folder as a workspace as soon as the
 * registry is up. The browser half still has to select it (selection lives in
 * the client); this just guarantees the folder is in the sidebar even if the
 * query-param open fails.
 */
export const inject = ["workspaceRegistry"];

export async function apply(ctx) {
	const path = process.env.AGENTDOCK_WORKSPACE?.trim();
	if (!path) return;
	try {
		await ctx.workspaceRegistry.create(path);
	} catch (error) {
		ctx.logger?.warn?.(`agentdock-workspace: ${String(error)}`);
	}
}
