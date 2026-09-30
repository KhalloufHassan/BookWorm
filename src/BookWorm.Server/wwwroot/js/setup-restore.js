// First-run setup: restore a backup instead of creating a new administrator.
(() => {
    const section = document.getElementById('setup-restore')
    if (!section) return
    const status = document.getElementById('restore-status')
    const input = document.getElementById('restore-file')
    const uploadButton = document.getElementById('restore-upload')

    const show = message => {
        status.hidden = false
        status.textContent = message
    }

    const disableAll = () => section.querySelectorAll('button, input').forEach(element => element.disabled = true)

    const confirmRestore = name => window.confirm(
        `Restore ${name}?\n\nBookWorm restarts to restore it. All accounts, libraries and files in the backup come back.`)

    // The app stops, restores while it starts again, then answers again: reload then.
    const waitForRestart = async () => {
        const started = Date.now()
        let wentDown = false
        for (;;) {
            await new Promise(resolve => setTimeout(resolve, 2000))
            if (Date.now() - started > 60_000) {
                show('Still restoring… Large libraries take longer. If BookWorm doesn\'t come back by itself (for example when it isn\'t run with Docker Compose), start it again: the restore runs as it starts.')
            }
            try {
                const response = await fetch('api/server-info', { cache: 'no-store' })
                if (response.ok && (wentDown || Date.now() - started > 20_000)) {
                    location.href = './'
                    return
                }
            } catch {
                wentDown = true
            }
        }
    }

    const restore = async name => {
        show(`Restoring ${name}… BookWorm is restarting. This page reloads when it's back.`)
        const response = await fetch(`api/setup/backups/${encodeURIComponent(name)}/restore`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ confirm: true }),
        })
        if (response.status !== 202) {
            const problem = await response.json().catch(() => null)
            const detail = problem?.errors ? Object.values(problem.errors).flat().join(' ') : problem?.detail
            throw new Error(detail || `The server answered ${response.status}.`)
        }
        await waitForRestart()
    }

    const run = async action => {
        disableAll()
        try {
            await action()
        } catch (error) {
            show(`Restoring didn't start: ${error.message}`)
            section.querySelectorAll('button, input').forEach(element => element.disabled = false)
            uploadButton.disabled = !input.files?.length
        }
    }

    section.querySelectorAll('[data-restore]').forEach(button => button.addEventListener('click', () => {
        const name = button.dataset.restore
        if (confirmRestore(name)) run(() => restore(name))
    }))

    input.addEventListener('change', () => uploadButton.disabled = !input.files?.length)

    uploadButton.addEventListener('click', () => {
        const file = input.files?.[0]
        if (!file || !confirmRestore(file.name)) return
        run(() => new Promise((resolve, reject) => {
            const request = new XMLHttpRequest()
            request.open('PUT', 'api/setup/backups/upload')
            request.setRequestHeader('Content-Type', 'application/gzip')
            request.upload.addEventListener('progress', event => {
                if (event.lengthComputable) show(`Uploading… ${Math.floor(event.loaded / event.total * 100)}%`)
            })
            request.addEventListener('load', () => {
                if (request.status !== 200) {
                    let detail = null
                    try {
                        const problem = JSON.parse(request.responseText)
                        detail = problem.errors ? Object.values(problem.errors).flat().join(' ') : problem.detail
                    } catch { /* not JSON */ }
                    reject(new Error(detail || `The upload failed (${request.status}).`))
                    return
                }
                restore(JSON.parse(request.responseText).name).then(resolve, reject)
            })
            request.addEventListener('error', () => reject(new Error('The upload failed. Check your connection.')))
            request.send(file)
        }))
    })
})()
