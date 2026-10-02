// Smoke: 2 connections, one ping per second each, 30 s.
// k6 run tests/load/smoke.js   (BASE_URL defaults to http://127.0.0.1:5180)
import { pingSession } from './lib/signalr.js';

export const options = {
	vus: 2,
	duration: '30s',
	thresholds: {
		pong_latency: ['p(95)<200'],
		ping_errors: ['rate<0.01'],
		checks: ['rate>0.99'],
	},
};

export default function () {
	pingSession(10);
}
