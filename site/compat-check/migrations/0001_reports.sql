CREATE TABLE reports (
	id TEXT PRIMARY KEY,
	day TEXT NOT NULL,
	browser TEXT NOT NULL,
	browser_major INTEGER,
	os TEXT NOT NULL,
	verdict TEXT NOT NULL,
	checks TEXT NOT NULL
);
