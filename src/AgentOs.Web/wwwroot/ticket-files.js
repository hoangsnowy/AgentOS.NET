// Ticket files (ADR-0015) — the one thing Blazor's InputFile doesn't pass to the server: where each file sits inside an
// uploaded folder. A folder picked with webkitdirectory gives every File a webkitRelativePath ("art/logo.png"); Blazor
// only sends the name. This returns those paths in the order InputFile lists the files (the input's own order).
window.agentosTicketFiles = {
    relativePaths(input) {
        if (!input || !input.files) { return []; }
        return Array.from(input.files, f => f.webkitRelativePath || f.name);
    },
};
