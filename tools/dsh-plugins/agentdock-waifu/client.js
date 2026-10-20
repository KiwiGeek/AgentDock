window.__ModuleLoader__.load({
	id: "@agentdock/dsh-waifu",
	factory() {
		/**
		 * Soften the opaque shell fills so the fixed background shows through,
		 * matching Agent Dock's flyout hover picture (right-anchored, covered).
		 */
		function apply(ctx) {
			const disposeTokens = ctx.theme.overrideTokens("@agentdock/dsh-waifu", {
				"--dsw-alias-bg-base": {
					light: "color-mix(in srgb, #ffffff 78%, transparent)",
					dark: "color-mix(in srgb, #151517 72%, transparent)",
				},
				"--dsw-specific-sidebar-fill": {
					light: "color-mix(in srgb, #f5f6f8 70%, transparent)",
					dark: "color-mix(in srgb, #1a1b1e 68%, transparent)",
				},
			});

			const style = document.createElement("style");
			style.setAttribute("data-agentdock-waifu", "");
			style.textContent = [
				"html body{",
				"background-image:url('/agentdock/waifu.png');",
				"background-position:right center;",
				"background-size:cover;",
				"background-repeat:no-repeat;",
				"background-attachment:fixed;",
				"}",
				/* Keep chat text readable over her. */
				"[data-shell-overlay],.pI_x6G_centerCol{",
				"backdrop-filter:blur(0px);",
				"}",
			].join("");
			document.head.appendChild(style);

			ctx.effect(() => () => {
				disposeTokens();
				style.remove();
			}, "agentdock-waifu:css");
		}

		return {
			inject: ["theme"],
			apply,
		};
	},
});
