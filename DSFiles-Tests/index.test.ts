import { describe, expect, test } from "bun:test";

describe("DS Upload handshake", async () => {
	const file = Bun.file(
		"C:\\Users\\Mrgaton\\Downloads\\怪獣になりたい _ Sakuzyo feat. 初音ミク 【プロジェクトセカイChampionship 2025】【MV】_3229.mp4",
	);
	//const file = Bun.file("index.test.ts");
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
		const CHUNK_SIZE = 9999744; // 10 MB
		const totalChunks = Math.ceil(file.size / CHUNK_SIZE);

		for (let i = 0; i < totalChunks; i++) {
			const start = i * CHUNK_SIZE;
			const end = Math.min(file.size, start + CHUNK_SIZE);
			const chunk = file.slice(start, end);

			response = await fetch("http://127.0.0.1:8081/cuc", {
				method: "POST",
				headers: {
					"Session-ID": session,
					chunk: String(i + 1),
				},
				body: chunk,
			});

			console.debug(response.headers);
			console.debug(await response.text());
		}

		expect(response.status).toBe(200);
	});
});
