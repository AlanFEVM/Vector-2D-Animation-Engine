import { copyFile, cp, mkdir } from 'node:fs/promises';

const publicDir = new URL('../public/', import.meta.url);
const docs = new URL('../../', import.meta.url);
await mkdir(new URL('manual/', publicDir), { recursive: true });
await Promise.all([
	copyFile(new URL('manual/index.html', docs), new URL('manual/index.html', publicDir)),
	cp(new URL('manual/images/', docs), new URL('manual/images/', publicDir), { recursive: true }),
	copyFile(new URL('USER_GUIDE.md', docs), new URL('USER_GUIDE.md', publicDir)),
	copyFile(new URL('../assets/branding/vector2d-icon.ico', docs), new URL('vector2d.ico', publicDir)),
]);
