'use strict';
// Placeholder until the UI design is chosen: proves the page, the token and the API work end to end.
const token = new URLSearchParams(location.hash.slice(1)).get('t') || '';
async function call(tool, args) {
  const response = await fetch('/api/call', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CodeReach-Token': token },
    body: JSON.stringify({ tool, args }),
  });
  if (!response.ok) throw new Error(await response.text());
  return response.json();
}
call('routes', { limit: 1000 }).then(result => {
  const pre = document.createElement('pre');
  pre.textContent = result.text;
  document.getElementById('app').replaceChildren(pre);
}).catch(error => {
  const p = document.createElement('p');
  p.textContent = 'CodeReach UI could not load: ' + error.message;
  document.getElementById('app').replaceChildren(p);
});
