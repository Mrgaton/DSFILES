import { describe, expect, test } from "bun:test";

describe("DS Upload handshake", async () => {
	const file = Bun.file("怪獣になりたい初音ミクプロジェクトセカイ.mp4");
	let response = await fetch("http://127.0.0.1:8081/cuh", {
		method: "POST",
		headers: {
			webhook:
				"https://discord.com/api/webhooks/1234940367183413331/geLDDQL2bRtLU0KDJBYQZXdkX8Hro3J0egEb-1Cqfyxu52EleBm59WYj5xXRKL0jQfg_",
			filename: "file.mp4",
			filesize: file.size,
		},
	});

	console.debug(response.headers);

	const session: string = response.headers.get("Session-ID") ?? "";

	expect(response.status).toBe(200);
	expect(session !== "").toBe(true);

	test("DS Upload chunk", async () => {
		response = await fetch("http://127.0.0.1:8081/cuc", {
			method: "POST",
			headers: {
				"Session-ID": session,
				"Content-Length": file.size,
				chunk: "1",
			},

			body: await file.arrayBuffer(),
		});

		console.debug(response.headers);
		console.debug(await response.text());
		expect(response.status).toBe(200);
	});
});
