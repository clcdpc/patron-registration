(() => {
    function create(getCurrentValue) {
        let generation = 0;

        function isCurrent(request) {
            return request.generation === generation && getCurrentValue() === request.value;
        }

        return {
            request(value, work) {
                const request = { generation: ++generation, value };
                return Promise.resolve()
                    .then(() => work(value))
                    .then(result => isCurrent(request) ? result : { status: "stale" })
                    .catch(() => isCurrent(request) ? { status: "unavailable" } : { status: "stale" });
            },
            invalidate() {
                generation++;
            }
        };
    }

    globalThis.AgeBlockRequestCoordinator = { create };
})();
