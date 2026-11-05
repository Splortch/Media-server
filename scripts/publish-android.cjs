// Android artifacts are published exclusively by the unified release coordinator.
module.exports = async () => { throw new Error('Standalone Android publication is disabled; use Automatic prereleases.'); };
