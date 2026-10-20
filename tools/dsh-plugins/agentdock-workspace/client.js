window.__ModuleLoader__.load({
	id: "@agentdock/dsh-workspace",
	factory() {
		const PARAM = "agentdock_workspace";

		/**
		 * Path Agent Dock asked us to open. Lives in the URL hash — dsh's launch-token
		 * handshake 303-redirects to "./" and drops every query param (including ours).
		 * Fragments survive that redirect.
		 */
		function requestedPath() {
			try {
				const hash = location.hash.startsWith("#") ? location.hash.slice(1) : location.hash;
				const fromHash = new URLSearchParams(hash).get(PARAM);
				if (fromHash) return fromHash;
				return new URLSearchParams(location.search).get(PARAM);
			} catch {
				return null;
			}
		}

		function clearParam() {
			try {
				if (location.hash.includes(PARAM)) {
					history.replaceState(null, "", location.pathname + location.search);
				}
			} catch {
				// leave the URL alone
			}
		}

		function whenReady(list, run) {
			let tries = 0;
			const tick = () => {
				const snap = list.getSnapshot?.() ?? list;
				if (snap?.phase === "ready") {
					run();
					return;
				}
				if (++tries < 80) setTimeout(tick, 100);
			};
			tick();
		}

		function pathsMatch(a, b) {
			const norm = (p) => String(p).replace(/\//g, "\\").replace(/\\+$/, "").toLowerCase();
			return norm(a) === norm(b);
		}

		async function openFolder(workspaces, uiWorkspace, path) {
			// Host plugin usually already registered it; create is idempotent by path.
			const workspace = await workspaces.create({ path });
			const id = workspace?.workspaceId;
			if (!id) {
				const snap = workspaces.list.getSnapshot();
				const existing = snap?.items?.find((item) => pathsMatch(item.path, path));
				if (!existing) throw new Error("create returned no workspaceId");
				await selectWorkspace(uiWorkspace, existing.workspaceId);
				return;
			}
			await selectWorkspace(uiWorkspace, id);
			clearParam();
		}

		/**
		 * Beat uiWorkspace.restoreSelection: it also runs when the lists go ready and
		 * would otherwise re-open the previous session. Wait briefly, open, then nudge
		 * once more in case restore finished after us.
		 */
		async function selectWorkspace(uiWorkspace, workspaceId) {
			await delay(600);
			uiWorkspace.startSession(workspaceId);
			await delay(800);
			uiWorkspace.startSession(workspaceId);
		}

		function delay(ms) {
			return new Promise((r) => setTimeout(r, ms));
		}

		return {
			inject: ["workspaces", "uiWorkspace"],
			apply(ctx) {
				const path = requestedPath();
				if (!path) return;
				whenReady(ctx.workspaces.list, () => {
					openFolder(ctx.workspaces, ctx.uiWorkspace, path).catch((error) => {
						console.warn("agentdock-workspace:", error);
					});
				});
			},
		};
	},
});
