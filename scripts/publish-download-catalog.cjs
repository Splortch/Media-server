const fs = require('node:fs');
const path = require('node:path');
const BRANCH = 'downloads';
const FILES = ['README.md', 'index.html', 'styles.css', 'app.js', '.nojekyll', 'catalog-source.json'];

// Publish generated files to a separate catalog branch. Never commit catalog refreshes to main.
module.exports = async function publish({github, context, directory = 'artifacts/downloads'}) {
  if (context.eventName === 'pull_request' || context.ref !== 'refs/heads/main') throw new Error('Catalog publication requires trusted main.');
  const source = JSON.parse(fs.readFileSync(path.join(directory, 'catalog-source.json'), 'utf8'));
  if (source.repository !== `${context.repo.owner}/${context.repo.repo}` || source.sha !== context.sha || !/^[a-f0-9]{40}$/.test(source.sha)) throw new Error('Catalog source identity mismatch.');
  const main = (await github.rest.repos.getBranch({...context.repo, branch: 'main'})).data;
  if (main.commit.sha !== context.sha) return false;
  let previous;
  try { previous = (await github.rest.git.getRef({...context.repo, ref: `heads/${BRANCH}`})).data.object.sha; }
  catch (e) { if (e.status !== 404) throw e; }
  let baseTree;
  if (previous) {
    // Do not replace an unrelated branch that happens to share our generated branch name.
    const marker = (await github.rest.repos.getContent({...context.repo, path: 'catalog-source.json', ref: previous})).data;
    const identity = JSON.parse(Buffer.from(marker.content, 'base64').toString('utf8'));
    if (identity.repository !== source.repository || identity.generated !== true) throw new Error('Existing downloads branch is not a managed catalog.');
    baseTree = (await github.rest.git.getCommit({...context.repo, commit_sha: previous})).data.tree.sha;
  }
  const tree = (await github.rest.git.createTree({...context.repo, ...(baseTree ? {base_tree: baseTree} : {}), tree: FILES.map(name => ({path: name, mode: '100644', type: 'blob', content: fs.readFileSync(path.join(directory, name), 'utf8')}))})).data;
  if (tree.sha === baseTree) return false;
  const commit = (await github.rest.git.createCommit({...context.repo, message: 'docs(downloads): refresh categorized release catalog', tree: tree.sha, parents: previous ? [previous] : []})).data;
  if (previous) {
    // A concurrent writer is not overwritten: updateRef requires fast-forward ancestry.
    await github.rest.git.updateRef({...context.repo, ref: `heads/${BRANCH}`, sha: commit.sha, force: false});
  } else await github.rest.git.createRef({...context.repo, ref: `refs/heads/${BRANCH}`, sha: commit.sha});
  return true;
};
