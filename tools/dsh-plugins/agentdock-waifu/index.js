/**
 * Host half: serve the waifu PNG at /agentdock/waifu.png behind the same
 * browser-trust fence as the rest of the web UI (cookie after the launch token).
 */
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

export const inject = ["webServer", "connection"];

const PATH = "/agentdock/waifu.png";

export function apply(ctx) {
	const png = process.env.AGENTDOCK_WAIFU_PATH?.trim()
		|| join(dirname(fileURLToPath(import.meta.url)), "waifu.png");

	const rejected = (req, res) => {
		const rejection = ctx.connection?.requestRejection?.(req);
		if (rejection === void 0) return false;
		res.statusCode = rejection;
		res.end();
		return true;
	};

	ctx.effect(() => ctx.webServer.register({
		kind: "exact",
		path: PATH,
		handler: async (req, res) => {
			if (rejected(req, res)) return;
			if (req.method !== "GET" && req.method !== "HEAD") {
				res.statusCode = 405;
				res.setHeader("allow", "GET, HEAD");
				res.end();
				return;
			}
			try {
				const body = await readFile(png);
				res.statusCode = 200;
				res.setHeader("content-type", "image/png");
				res.setHeader("cache-control", "private, max-age=3600");
				if (req.method === "HEAD") {
					res.setHeader("content-length", String(body.length));
					res.end();
					return;
				}
				res.end(body);
			} catch {
				res.statusCode = 404;
				res.end();
			}
		},
	}), `agentdock-waifu: GET ${PATH}`);
}
