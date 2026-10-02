// Minimal SignalR client for k6: JSON hub protocol over a WebSocket, no negotiate step.
// Wire format: every message is a JSON object followed by the record separator (0x1E).
import { check } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import ws from 'k6/ws';

const RS = '\x1e';
const INVOCATION = 1;
const CLOSE = 7;

export const baseUrl = __ENV.BASE_URL || 'http://127.0.0.1:5180';

/** Ping sent -> matching pong received, in milliseconds. */
export const pongLatency = new Trend('pong_latency', true);

/** Share of pings that got no pong (plus failed connections). */
export const pingErrors = new Rate('ping_errors');

/**
 * Opens one hub connection, sends a `ping` Envelope every second for `seconds`,
 * waits up to 5 s for outstanding pongs, then closes.
 */
export function pingSession(seconds) {
	const url = `${baseUrl.replace(/^http/, 'ws')}/hub`;
	const pending = new Map();
	let seq = 0;
	let draining = false;

	const res = ws.connect(url, null, (socket) => {
		const sendPing = () => {
			if (draining) {
				return;
			}

			const correlationId = `${__VU}-${__ITER}-${seq++}`;
			pending.set(correlationId, Date.now());
			socket.send(JSON.stringify({
				type: INVOCATION,
				target: 'Send',
				arguments: [{ type: 'ping', payload: {}, correlationId }],
			}) + RS);
		};

		const onReceive = (envelope) => {
			const sentAt = pending.get(envelope.correlationId);
			if (sentAt === undefined) {
				return;
			}

			pending.delete(envelope.correlationId);
			const ok = check(envelope, {
				'pong with serverTime': (e) => e.type === 'pong' && typeof e.payload.serverTime === 'string',
			});
			if (ok) {
				pongLatency.add(Date.now() - sentAt);
			}

			pingErrors.add(!ok);
			if (draining && pending.size === 0) {
				socket.close();
			}
		};

		socket.on('open', () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS));

		socket.on('message', (data) => {
			for (const frame of data.split(RS)) {
				if (!frame) {
					continue;
				}

				const message = JSON.parse(frame);
				if (message.type === undefined) {
					// Handshake response: {} on success, {"error":"..."} on failure.
					if (check(message, { 'handshake accepted': (m) => !m.error })) {
						sendPing();
						socket.setInterval(sendPing, 1000);
					} else {
						socket.close();
					}
				} else if (message.type === INVOCATION && message.target === 'Receive') {
					onReceive(message.arguments[0]);
				} else if (message.type === CLOSE) {
					socket.close();
				}
			}
		});

		socket.setTimeout(() => {
			draining = true;
			if (pending.size === 0) {
				socket.close();
			}
		}, seconds * 1000);
		socket.setTimeout(() => socket.close(), (seconds + 5) * 1000);
	});

	const connected = check(res, { 'websocket upgraded (101)': (r) => r && r.status === 101 });
	if (!connected) {
		pingErrors.add(true);
	}

	for (let i = 0; i < pending.size; i++) {
		pingErrors.add(true);
	}
}
