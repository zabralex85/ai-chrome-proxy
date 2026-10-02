// Load: ramp 0 -> 200 concurrent connections over 1 min, hold 2 min, ramp down; one ping per second each.
// k6 run tests/load/load.js   (BASE_URL defaults to http://127.0.0.1:5180)
// DURATION_SCALE=0.1 shortens every stage proportionally for a quick check.
import { pingSession } from './lib/signalr.js';

const scale = Number(__ENV.DURATION_SCALE || 1);
const stage = (seconds) => `${Math.max(1, Math.round(seconds * scale))}s`;

export const options = {
	stages: [
		{ duration: stage(60), target: 200 },
		{ duration: stage(120), target: 200 },
		{ duration: stage(30), target: 0 },
	],
	thresholds: {
		pong_latency: ['p(95)<500'],
		ping_errors: ['rate<0.01'],
		checks: ['rate>0.99'],
	},
};

export default function () {
	pingSession(10);
}
