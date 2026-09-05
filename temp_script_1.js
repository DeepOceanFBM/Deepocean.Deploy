
        let availableProcesses = [];
        let allProjects = [];
        let editingProjectIndex = -1;

        // --- Navigation ---
        function switchTab(pageId, el) {
            document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
            document.getElementById(pageId).classList.add('active');

            document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
            el.classList.add('active');

            if (pageId === 'manage-page') {
                renderManageProjects();
            } else if (pageId === 'custom-tools-page') {
                initMonaco();
                loadCtTools();
            } else {
                renderRunProjects();
            }
        }

        // --- API Calls ---
        async function fetchProcesses() {
            try {
                const res = await fetch('/api/processes');
                availableProcesses = await res.json();

                const select = document.getElementById('processTypeSelect');
                select.innerHTML = '';
                availableProcesses.forEach(proc => {
                    const opt = document.createElement('option');
                    opt.value = proc.Name;
                    opt.textContent = proc.Name;
                    select.appendChild(opt);
                });
            } catch (err) {
                console.error("Failed to load processes", err);
            }
        }

        async function fetchProjects() {
            try {
                const res = await fetch('/api/projects');
                allProjects = await res.json() || [];
                renderRunProjects();
            } catch (err) {
                console.error("Failed to load projects", err);
                allProjects = [];
            }
        }

        async function saveProjectsToServer() {
            try {
                await fetch('/api/projects', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(allProjects)
                });
            } catch (err) {
                console.error("Failed to save projects", err);
            }
        }

        // --- Init ---
        async function init() {
            await fetchProcesses();
            await fetchProjects();
        }

        // --- Run Projects ---
        function renderRunProjects() {
            const grid = document.getElementById('runProjectsGrid');
            grid.innerHTML = '';

            allProjects.forEach(proj => {
                const lbl = document.createElement('label');
                lbl.className = 'run-project-card';

                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.value = proj.ProjectName;

                const span = document.createElement('span');
                span.textContent = proj.ProjectName + (proj.ProjectType ? ` [${proj.ProjectType}]` : '');

                lbl.appendChild(cb);
                lbl.appendChild(span);
                grid.appendChild(lbl);
            });
        }

        let isDeploying = false;
        let logPollInterval = null;

        async function startDeployment() {
            if (isDeploying) return;

            const checkboxes = document.querySelectorAll('#runProjectsGrid input[type="checkbox"]:checked');
            const projects = Array.from(checkboxes).map(cb => cb.value);

            if (projects.length === 0) {
                alert("Please select at least one project.");
                return;
            }

            isDeploying = true;
            document.getElementById('deployBtn').disabled = true;
            document.getElementById('deployBtn').textContent = 'Deploying...';
            document.getElementById('logsContainer').innerHTML = '';

            logPollInterval = setInterval(pollLogs, 1000);

            try {
                await fetch('/api/deploy', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ Projects: projects })
                });
            } catch (err) {
                console.error(err);
            } finally {
                isDeploying = false;
                document.getElementById('deployBtn').disabled = false;
                document.getElementById('deployBtn').textContent = 'Start Deployment ⚡';
                setTimeout(pollLogs, 1000);
                setTimeout(() => clearInterval(logPollInterval), 2000);
            }
        }

        async function pollLogs() {
            try {
                const response = await fetch('/api/logs');
                const data = await response.json();
                const container = document.getElementById('logsContainer');

                let formattedHtml = '';
                data.logs.forEach(log => {
                    if (log.includes('❌') || log.toLowerCase().includes('error')) {
                        formattedHtml += `<span class="log-error">${log}</span><br>`;
                    } else if (log.includes('✅')) {
                        formattedHtml += `<span class="log-success">${log}</span><br>`;
                    } else {
                        formattedHtml += `<span class="log-info">${log}</span><br>`;
                    }
                });

                if (container.innerHTML !== formattedHtml) {
                    container.innerHTML = formattedHtml;
                    container.scrollTop = container.scrollHeight;
                }
            } catch (e) { }
        }

        // --- Manage Projects ---
        function renderManageProjects() {
            const list = document.getElementById('manageProjectsList');
            list.innerHTML = '';

            if (allProjects.length === 0) {
                list.innerHTML = '<p style="color: var(--text-muted)">No projects found. Create one!</p>';
                return;
            }

            allProjects.forEach((proj, idx) => {
                const div = document.createElement('div');
                div.className = 'project-list-item';
                div.innerHTML = `
                    <div>
                        <strong>${proj.ProjectName}</strong> ${proj.ProjectType ? `<span style="color:var(--primary-color); font-size:12px; margin-left: 5px;">[${proj.ProjectType}]</span>` : ''} <span style="color:var(--text-muted); font-size:12px;">(ID: ${proj.ProjectID})</span>
                        <div style="font-size: 12px; color: var(--primary-color); margin-top: 5px;">${proj.Processes ? proj.Processes.length : 0} Processes</div>
                    </div>
                    <div class="actions">
                        <button class="secondary" onclick="editProject(${idx})">Edit</button>
                        <button class="danger" onclick="deleteProject(${idx})">Delete</button>
                    </div>
                `;
                list.appendChild(div);
            });
        }

        function openProjectModal(editIdx = -1) {
            editingProjectIndex = editIdx;
            const modal = document.getElementById('projectModal');
            const processesContainer = document.getElementById('processesContainer');
            processesContainer.innerHTML = '';

            if (editIdx >= 0) {
                const proj = allProjects[editIdx];
                document.getElementById('modalTitle').textContent = 'Edit Project';
                document.getElementById('projNameInput').value = proj.ProjectName || '';
                document.getElementById('projTypeInput').value = proj.ProjectType || '';
                document.getElementById('projIdInput').value = proj.ProjectID || 0;

                if (proj.Processes) {
                    proj.Processes.forEach(proc => addProcessUI(proc.Type, proc));
                }
            } else {
                document.getElementById('modalTitle').textContent = 'New Project';
                document.getElementById('projNameInput').value = '';
                document.getElementById('projTypeInput').value = '';
                document.getElementById('projIdInput').value = 0;
            }

            modal.classList.add('active');
        }

        function closeProjectModal() {
            document.getElementById('projectModal').classList.remove('active');
        }

        function addProcessFromSelect() {
            const type = document.getElementById('processTypeSelect').value;
            if (type) addProcessUI(type, {});
        }

        function updateProcessIndices() {
            document.querySelectorAll('.process-data-block').forEach((card, i) => {
                const badge = card.querySelector('.order-idx');
                if (badge) badge.textContent = i + 1;
            });
        }

        function reorderProcess(btn, direction) {
            const card = btn.closest('.process-data-block');
            const container = card.parentElement;
            if (direction === 'up' && card.previousElementSibling) {
                container.insertBefore(card, card.previousElementSibling);
            } else if (direction === 'down' && card.nextElementSibling) {
                container.insertBefore(card.nextElementSibling, card);
            }
            updateProcessIndices();
        }

        function addProcessUI(type, initialData = {}) {
            const procMeta = availableProcesses.find(p => p.Name === type);
            if (!procMeta) return;

            const container = document.getElementById('processesContainer');
            const currentIndex = container.querySelectorAll('.process-data-block').length + 1;
            const card = document.createElement('div');
            card.className = 'process-card process-data-block';
            card.dataset.type = type;

            let html = `
                <div class="process-order-controls">
                    <button class="order-btn" onclick="reorderProcess(this, 'up')" title="Move Up">▲</button>
                    <span class="order-idx">${currentIndex}</span>
                    <button class="order-btn" onclick="reorderProcess(this, 'down')" title="Move Down">▼</button>
                </div>
                <h4>⚙️ ${type}</h4>
                <button class="remove-process" onclick="this.closest('.process-data-block').remove(); updateProcessIndices();">✕</button>
            `;

            html += buildPropertyFields(procMeta.Properties, initialData, '');

            card.innerHTML = html;
            container.appendChild(card);
        }

        function buildPropertyFields(properties, initialData, pathPrefix) {
            let html = '';
            if (!properties) return html;

            properties.forEach(prop => {
                if (prop.Name === 'Type' || prop.Name === 'IndexRuning') return;

                const fullPath = pathPrefix ? `${pathPrefix}.${prop.Name}` : prop.Name;
                const val = initialData && initialData[prop.Name] !== undefined ? initialData[prop.Name] : '';

                if (prop.Type === 'Object' && prop.Fields) {
                    // Nested Object
                    html += `<fieldset style="margin-bottom:10px; border:1px solid rgba(255,255,255,0.2); border-radius:5px; padding:10px;">
                        <legend style="color:var(--primary-color); font-size:12px; padding:0 5px;">${prop.Name}</legend>
                        ${buildPropertyFields(prop.Fields, val || {}, fullPath)}
                    </fieldset>`;
                } else {
                    // Primitive
                    html += `<div class="form-group">
                        <label>${prop.Name} (${prop.Type})</label>`;

                    if (prop.Type === 'Boolean' || prop.Type === 'bool') {
                        const checked = val ? 'checked' : '';
                        html += `<input type="checkbox" data-prop="${fullPath}" data-type="${prop.Type}" ${checked}>`;
                    } else if (prop.Type === 'Int32' || prop.Type === 'int') {
                        html += `<input type="number" data-prop="${fullPath}" data-type="${prop.Type}" value="${val}">`;
                    } else {
                        html += `<input type="text" data-prop="${fullPath}" data-type="${prop.Type}" value="${val}">`;
                    }
                    html += `</div>`;
                }
            });
            return html;
        }

        async function saveProject() {
            const name = document.getElementById('projNameInput').value;
            const typeValue = document.getElementById('projTypeInput').value;
            const id = parseInt(document.getElementById('projIdInput').value) || 0;

            if (!name) {
                alert("Project Name is required.");
                return;
            }

            const processes = [];
            document.querySelectorAll('.process-data-block').forEach((card, cardIndex) => {
                const type = card.dataset.type;
                const procMeta = availableProcesses.find(p => p.Name === type);

                const procObj = { Type: type, IndexRuning: cardIndex + 1 };

                const inputs = card.querySelectorAll('input[data-prop]');
                inputs.forEach(input => {
                    const path = input.getAttribute('data-prop');
                    const propType = input.getAttribute('data-type');
                    let val;

                    if (propType === 'Boolean' || propType === 'bool') {
                        val = input.checked;
                    } else if (propType === 'Int32' || propType === 'int') {
                        val = parseInt(input.value) || 0;
                    } else {
                        val = input.value;
                    }

                    // Set nested value
                    const parts = path.split('.');
                    let current = procObj;
                    for (let i = 0; i < parts.length - 1; i++) {
                        if (!current[parts[i]]) current[parts[i]] = {};
                        current = current[parts[i]];
                    }
                    current[parts[parts.length - 1]] = val;
                });
                processes.push(procObj);
            });

            const newProj = {
                ProjectName: name,
                ProjectType: typeValue,
                ProjectID: id,
                Processes: processes
            };

            if (editingProjectIndex >= 0) {
                allProjects[editingProjectIndex] = newProj;
            } else {
                allProjects.push(newProj);
            }

            await saveProjectsToServer();
            closeProjectModal();
            renderManageProjects();
        }

        function editProject(idx) {
            openProjectModal(idx);
        }

        async function deleteProject(idx) {
            if (confirm("Are you sure you want to delete this project?")) {
                allProjects.splice(idx, 1);
                await saveProjectsToServer();
                renderManageProjects();
            }
        }

        // --- Export / Import ---
        function exportProjects() {
            const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(allProjects, null, 4));
            const downloadAnchorNode = document.createElement('a');
            downloadAnchorNode.setAttribute("href", dataStr);
            downloadAnchorNode.setAttribute("download", "projects_backup.json");
            document.body.appendChild(downloadAnchorNode);
            downloadAnchorNode.click();
            downloadAnchorNode.remove();
        }

        function importProjects(event) {
            const file = event.target.files[0];
            if (!file) return;
            const reader = new FileReader();
            reader.onload = async function (e) {
                try {
                    const contents = e.target.result;
                    const parsed = JSON.parse(contents);
                    if (!Array.isArray(parsed)) {
                        alert("Invalid format: File must contain a JSON array of projects.");
                        return;
                    }
                    if (parsed.length > 0 && typeof parsed[0] === 'object' && !('ProjectName' in parsed[0])) {
                        alert("Invalid format: Missing 'ProjectName' property in array items.");
                        return;
                    }
                    allProjects = parsed;
                    await saveProjectsToServer();
                    renderManageProjects();
                    alert("Projects imported successfully!");
                } catch (err) {
                    alert("Failed to parse JSON file.");
                    console.error(err);
                }
                event.target.value = ''; // reset input
            };
            reader.readAsText(file);
        }

        function downloadLogs() {
            const container = document.getElementById('logsContainer');
            if (!container.innerText) {
                alert("No logs to download!");
                return;
            }
            const dataStr = "data:text/plain;charset=utf-8," + encodeURIComponent(container.innerText);
            const downloadAnchorNode = document.createElement('a');
            downloadAnchorNode.setAttribute("href", dataStr);
            downloadAnchorNode.setAttribute("download", "deployment_logs.txt");
            document.body.appendChild(downloadAnchorNode);
            downloadAnchorNode.click();
            downloadAnchorNode.remove();
        }

        // ─── Custom Tools ─────────────────────────────────────────────────────

        let monacoModelEditor = null;
        let monacoWorkflowEditor = null;
        let ctCurrentTab = 'model';
        let ctCurrentTool = null;
        let ctPackages = [];
        let monacoReady = false;

        const MODEL_TEMPLATE = `namespace DeepOcean.Deploy.Tools
{
    public class MyCustomTool : EventTools
    {
        public string MyProperty { get; set; } = string.Empty;
    }
}`;

        const WORKFLOW_TEMPLATE = `using DeepOcean.Deploy.Tools;

namespace DeepOcean.Deploy.WorkFlow
{
    internal class MyCustomTool_WorkFlow
    {
        public static object Start(MyCustomTool Config)
        {
            // Your logic here
            return true;
        }
    }
}`;

        function initMonaco() {
            if (monacoReady) return;
            require.config({ paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.44.0/min/vs' } });
            require(['vs/editor/editor.main'], function () {
                monacoReady = true;

                // Dark theme matching our UI
                monaco.editor.defineTheme('deepocean', {
                    base: 'vs-dark',
                    inherit: true,
                    rules: [],
                    colors: { 'editor.background': '#0a0f1e' }
                });

                monacoModelEditor = monaco.editor.create(document.getElementById('monacoEditorModel'), {
                    value: MODEL_TEMPLATE,
                    language: 'csharp',
                    theme: 'deepocean',
                    fontSize: 13,
                    minimap: { enabled: false },
                    automaticLayout: true,
                });

                monacoWorkflowEditor = monaco.editor.create(document.getElementById('monacoEditorWorkflow'), {
                    value: WORKFLOW_TEMPLATE,
                    language: 'csharp',
                    theme: 'deepocean',
                    fontSize: 13,
                    minimap: { enabled: false },
                    automaticLayout: true,
                });

                // Two-way sync listeners
                monacoModelEditor.onDidChangeModelContent(() => {
                    if (isSyncingName) return;
                    syncNameFromEditor(monacoModelEditor.getValue(), 'model');
                });
                monacoWorkflowEditor.onDidChangeModelContent(() => {
                    if (isSyncingName) return;
                    syncNameFromEditor(monacoWorkflowEditor.getValue(), 'workflow');
                });
            });
        }

        let isSyncingName = false;

        function syncNameFromInput(newName) {
            if (!newName || !monacoModelEditor || !monacoWorkflowEditor) return;
            const safeName = newName.replace(/[^a-zA-Z0-9_]/g, '');
            if (!safeName) return;

            isSyncingName = true;

            // Sync Model Editor
            let modelCode = monacoModelEditor.getValue();
            modelCode = modelCode.replace(/class \w+/g, `class ${safeName}`);
            modelCode = modelCode.replace(/public \w+\s*\(/g, `public ${safeName}(`);
            if (modelCode !== monacoModelEditor.getValue()) {
                monacoModelEditor.setValue(modelCode);
            }

            // Sync Workflow Editor
            let wfCode = monacoWorkflowEditor.getValue();
            wfCode = wfCode.replace(/class \w+_WorkFlow/g, `class ${safeName}_WorkFlow`);
            wfCode = wfCode.replace(/Start\(\w+ /g, `Start(${safeName} `);
            if (wfCode !== monacoWorkflowEditor.getValue()) {
                monacoWorkflowEditor.setValue(wfCode);
            }

            isSyncingName = false;
        }

        function syncNameFromEditor(code, source) {
            isSyncingName = true;
            let extractedName = null;

            if (source === 'model') {
                const match = code.match(/class (\w+)/);
                if (match && match[1]) extractedName = match[1];
            } else if (source === 'workflow') {
                const match = code.match(/class (\w+)_WorkFlow/);
                if (match && match[1]) extractedName = match[1];
            }

            if (extractedName && extractedName !== 'EventTools' && extractedName !== 'MyCustomTool') {
                const nameInput = document.getElementById('ctToolName');
                if (nameInput.value !== extractedName) {
                    nameInput.value = extractedName;
                    // Sync the other editor
                    if (source === 'model') {
                        let wfCode = monacoWorkflowEditor.getValue();
                        wfCode = wfCode.replace(/class \w+_WorkFlow/g, `class ${extractedName}_WorkFlow`);
                        wfCode = wfCode.replace(/Start\(\w+ /g, `Start(${extractedName} `);
                        if (wfCode !== monacoWorkflowEditor.getValue()) monacoWorkflowEditor.setValue(wfCode);
                    } else {
                        let modelCode = monacoModelEditor.getValue();
                        modelCode = modelCode.replace(/class \w+/g, `class ${extractedName}`);
                        modelCode = modelCode.replace(/public \w+\s*\(/g, `public ${extractedName}(`);
                        if (modelCode !== monacoModelEditor.getValue()) monacoModelEditor.setValue(modelCode);
                    }
                }
            }
            isSyncingName = false;
        }

        async function loadCtTools() {
            try {
                const res = await fetch('/api/custom-tools');
                const tools = await res.json();
                const list = document.getElementById('ctToolList');
                list.innerHTML = '';
                tools.forEach(name => {
                    const item = document.createElement('div');
                    item.className = 'ct-tool-item' + (ctCurrentTool === name ? ' selected' : '');
                    item.innerHTML = `<span>${name}</span><span class="ct-del" onclick="deleteCtTool(event,'${name}')">✕</span>`;
                    item.addEventListener('click', () => openCtTool(name));
                    list.appendChild(item);
                });
            } catch (e) { console.error(e); }
        }

        async function openCtTool(name) {
            ctCurrentTool = name;
            const res = await fetch(`/api/custom-tools/${name}`);
            const tool = await res.json();

            document.getElementById('ctToolName').value = tool.Name || name;
            ctPackages = tool.Packages || [];
            renderCtPackages();

            if (monacoModelEditor) monacoModelEditor.setValue(tool.ModelCode || MODEL_TEMPLATE);
            if (monacoWorkflowEditor) monacoWorkflowEditor.setValue(tool.WorkFlowCode || WORKFLOW_TEMPLATE);

            showCtEditor();
            loadCtTools();
        }

        function newCustomTool() {
            ctCurrentTool = null;
            document.getElementById('ctToolName').value = '';
            ctPackages = [];
            renderCtPackages();

            if (monacoModelEditor) monacoModelEditor.setValue(MODEL_TEMPLATE);
            if (monacoWorkflowEditor) monacoWorkflowEditor.setValue(WORKFLOW_TEMPLATE);

            showCtEditor();
            document.querySelectorAll('.ct-tool-item').forEach(el => el.classList.remove('selected'));
        }

        function showCtEditor() {
            const header = document.getElementById('ctEditorHeader');
            header.style.display = 'flex';
            document.getElementById('ctEditorPlaceholder').style.display = 'none';
            switchCtTab('model');
        }

        function cancelCtEdit() {
            document.getElementById('ctEditorHeader').style.display = 'none';
            document.getElementById('ctEditorPlaceholder').style.display = 'flex';
            document.getElementById('monacoEditorModel').style.display = 'none';
            document.getElementById('monacoEditorWorkflow').style.display = 'none';
            ctCurrentTool = null;
            loadCtTools();
        }

        function switchCtTab(tab) {
            ctCurrentTab = tab;
            document.getElementById('tabModel').classList.toggle('active', tab === 'model');
            document.getElementById('tabWorkflow').classList.toggle('active', tab === 'workflow');
            document.getElementById('monacoEditorModel').style.display = tab === 'model' ? 'block' : 'none';
            document.getElementById('monacoEditorWorkflow').style.display = tab === 'workflow' ? 'block' : 'none';
            if (tab === 'model' && monacoModelEditor) monacoModelEditor.layout();
            if (tab === 'workflow' && monacoWorkflowEditor) monacoWorkflowEditor.layout();
        }

        async function saveCustomTool() {
            const name = document.getElementById('ctToolName').value.trim();
            if (!name) { alert('Tool name is required.'); return; }
            if (!/^[A-Za-z][A-Za-z0-9_]*$/.test(name)) { alert('Tool name must be a valid C# class name.'); return; }

            const payload = {
                Name: name,
                ModelCode: monacoModelEditor ? monacoModelEditor.getValue() : '',
                WorkFlowCode: monacoWorkflowEditor ? monacoWorkflowEditor.getValue() : '',
                Packages: ctPackages,
            };

            const res = await fetch('/api/custom-tools', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (res.ok) {
                ctCurrentTool = name;
                alert(`✅ Tool '${name}' saved successfully!`);
                await loadCtTools();
                // Also refresh available processes list for Manage Projects
                await fetchProcesses();
            } else {
                alert('❌ Failed to save tool.');
            }
        }

        async function compileCustomTool() {

            console.log("-------------------------------");

            const name = document.getElementById('ctToolName').value.trim();
            if (!name) { alert('Tool name is required to compile.'); return; }

            const payload = {
                Name: name,
                ModelCode: monacoModelEditor ? monacoModelEditor.getValue() : '',
                WorkFlowCode: monacoWorkflowEditor ? monacoWorkflowEditor.getValue() : '',
                Packages: ctPackages,
            };

            const btn = document.querySelector('button[onclick="compileCustomTool()"]');
            const originalText = btn.innerHTML;
            btn.innerHTML = '⚙️ Compiling...';
            btn.disabled = true;

            try {
                const res = await fetch('/api/custom-tools/compile', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload)
                });

                const result = await res.json();
                if (result.Success) {
                    console.log('✅ Compilation successful!\n\nLogs:\n' + result.Logs.join('\n'));
                    alert('✅ Compilation successful!\n\nLogs:\n' + result.Logs.join('\n'));
                } else {
                    console.log('❌ Compilation failed!\n\nError:\n' + result.Error + '\n\nLogs:\n' + result.Logs.join('\n'));
                    alert('❌ Compilation failed!\n\nError:\n' + result.Error + '\n\nLogs:\n' + result.Logs.join('\n'));
                }
            } catch (err) {
                alert('Error compiling tool: ' + err.message);
            } finally {
                btn.innerHTML = originalText;
                btn.disabled = false;
            }
        }

        function exportCtp() {
            const name = document.getElementById('ctToolName').value.trim();
            if (!name) { alert('Please save or name the tool first.'); return; }

            const payload = {
                Name: name,
                ModelCode: monacoModelEditor ? monacoModelEditor.getValue() : '',
                WorkFlowCode: monacoWorkflowEditor ? monacoWorkflowEditor.getValue() : '',
                Packages: ctPackages,
            };

            const jsonStr = JSON.stringify(payload);
            const base64 = btoa(unescape(encodeURIComponent(jsonStr))); // encode to make it look "complex"

            const blob = new Blob([base64], { type: 'application/ctp' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = name + '.ctp';
            a.click();
            URL.revokeObjectURL(url);
        }

        function triggerImportCtp() {
            document.getElementById('ctpFileInput').click();
        }

        async function handleImportCtp(event) {
            const file = event.target.files[0];
            if (!file) return;

            const reader = new FileReader();
            reader.onload = async function (e) {
                try {
                    const base64 = e.target.result;
                    const jsonStr = decodeURIComponent(escape(atob(base64)));
                    const tool = JSON.parse(jsonStr);

                    if (!tool.Name || !tool.ModelCode) throw new Error("Invalid .CTP file format.");

                    // Save directly to backend
                    const res = await fetch('/api/custom-tools', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(tool)
                    });

                    if (res.ok) {
                        alert(`✅ Tool '${tool.Name}' imported successfully!`);
                        await loadCtTools();
                        await fetchProcesses();
                        openCtTool(tool.Name); // open it in editor
                    } else {
                        alert('❌ Failed to import tool to server.');
                    }
                } catch (err) {
                    alert('❌ Invalid or corrupted .CTP file.\n\nError: ' + err.message);
                }
                // Reset file input
                document.getElementById('ctpFileInput').value = '';
            };
            reader.readAsText(file);
        }

        async function deleteCtTool(event, name) {
            event.stopPropagation();
            if (!confirm(`Delete tool '${name}'?`)) return;
            await fetch(`/api/custom-tools/${name}`, { method: 'DELETE' });
            if (ctCurrentTool === name) cancelCtEdit();
            await loadCtTools();
            await fetchProcesses();
        }

        function addCtPackage() {
            const input = document.getElementById('ctPkgInput');
            const val = input.value.trim();
            if (!val) return;
            if (!ctPackages.includes(val)) ctPackages.push(val);
            input.value = '';
            renderCtPackages();
        }

        function removeCtPackage(pkg) {
            ctPackages = ctPackages.filter(p => p !== pkg);
            renderCtPackages();
        }

        function renderCtPackages() {
            const container = document.getElementById('ctPkgList');
            container.innerHTML = '';
            ctPackages.forEach(pkg => {
                const tag = document.createElement('div');
                tag.className = 'ct-pkg-tag';
                tag.innerHTML = `${pkg} <span onclick="removeCtPackage('${pkg}')">✕</span>`;
                container.appendChild(tag);
            });
        }

        // Run init on load
        window.addEventListener('DOMContentLoaded', init);

        // ======================== DEBUGGER ========================
        let _debugTarget = null;   // { name, workflowCode, config }
        let _debugWs = null;
        let _debugEditor = null;
        let _debugDecorations = [];
        let _dbgSeq = 1;
        let _dbgThreadId = null;

        function setDebugTarget() {
            const name = document.getElementById('ctToolName').value;
            if (!name) { alert('Please open a tool first.'); return; }
            const workflowCode = monacoWorkflowEditor ? monacoWorkflowEditor.getValue() : '';
            _debugTarget = { name, workflowCode };
            // Update UI
            document.getElementById('debugTargetBoxName').textContent = name;
            document.getElementById('debugTargetBox').style.display = 'block';
            document.getElementById('debugTargetName').textContent = name;
            document.getElementById('debugTargetLabel').style.display = '';
            _dbgLog(`✅ Debug target set to: ${name}`);
        }

        function clearDebugTarget() {
            _debugTarget = null;
            document.getElementById('debugTargetBox').style.display = 'none';
            document.getElementById('debugTargetLabel').style.display = 'none';
        }

        function openDebugPanel() {
            const overlay = document.getElementById('debugOverlay');
            overlay.style.display = 'flex';
            if (!_debugTarget) {
                _dbgLog('⚠️ No debug target set. Go to Custom Tools and click "Set as Debug Target".');
            } else {
                _dbgLog(`Ready to debug: ${_debugTarget.name}`);
                _dbgLog('Click ⚙️ Compile first, then ▶ Run.');
            }
            // Init debug monaco editor if not done
            if (!_debugEditor && typeof monaco !== 'undefined') {
                _debugEditor = monaco.editor.create(document.getElementById('debugMonacoEditor'), {
                    value: _debugTarget ? _debugTarget.workflowCode : '// No target selected',
                    language: 'csharp',
                    theme: 'vs-dark',
                    readOnly: false,
                    minimap: { enabled: false },
                    glyphMargin: true,
                    lineNumbersMinChars: 4,
                    scrollBeyondLastLine: false,
                    automaticLayout: true,
                    lineDecorationsWidth: 5,
                });
                // Breakpoint: click on line numbers OR glyph margin
                _debugEditor.onMouseDown(e => {
                    const t = e.target.type;
                    if (
                        t === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN ||
                        t === monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS ||
                        t === monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS
                    ) {
                        _toggleBreakpoint(e.target.position.lineNumber);
                    }
                });
            } else if (_debugEditor && _debugTarget) {
                _debugEditor.setValue(_debugTarget.workflowCode);
                _debugDecorations = _debugEditor.deltaDecorations(_debugDecorations, []);
            }
            // Force layout after show
            setTimeout(() => { if (_debugEditor) _debugEditor.layout(); }, 50);

        function closeDebugPanel() {
            document.getElementById('debugOverlay').style.display = 'none';
        }

        // --- Breakpoints ---
        let _breakpoints = [];
        function _toggleBreakpoint(line) {
            const idx = _breakpoints.indexOf(line);
            if (idx >= 0) _breakpoints.splice(idx, 1);
            else _breakpoints.push(line);
            _renderBreakpoints();
            if (_debugWs && _debugWs.readyState === WebSocket.OPEN) {
                _dbgSendDap('setBreakpoints', {
                    source: { path: '__workflow__' },
                    breakpoints: _breakpoints.map(l => ({ line: l }))
                });
            }
        }

        function _renderBreakpoints() {
            if (!_debugEditor) return;
            // Merge breakpoint decorations + keep current-line decoration
            const bpDecorations = _breakpoints.map(line => ({
                range: new monaco.Range(line, 1, line, 1),
                options: {
                    isWholeLine: true,
                    glyphMarginClassName: 'dbg-breakpoint-glyph',
                    linesDecorationsClassName: 'dbg-bp-line-num',
                    overviewRuler: { color: '#ef4444', position: monaco.editor.OverviewRulerLane.Left }
                }
            }));
            _debugDecorations = _debugEditor.deltaDecorations(_debugDecorations, bpDecorations);
        }

        function _highlightLine(line) {
            if (!_debugEditor) return;
            _debugDecorations = _debugEditor.deltaDecorations(_debugDecorations,
                _breakpoints.map(l => ({
                    range: new monaco.Range(l, 1, l, 1),
                    options: { isWholeLine: true, glyphMarginClassName: 'dbg-breakpoint-glyph' }
                })).concat([{
                    range: new monaco.Range(line, 1, line, 1),
                    options: {
                        isWholeLine: true,
                        className: 'dbg-current-line',
                        glyphMarginClassName: 'dbg-arrow-glyph'
                    }
                }])
            );
            _debugEditor.revealLineInCenter(line);
        }

        // --- Compile for Debug ---
        async function dbgCompile() {
            if (!_debugTarget) { _dbgLog('❌ No debug target set.'); return; }
            _dbgSetStatus('COMPILING', '#f59e0b');
            _dbgLog(`⚙️ Compiling ${_debugTarget.name} for debug...`);
            try {
                const res = await fetch('/api/debug-compile', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name: _debugTarget.name })
                });
                const data = await res.json();
                data.Logs?.forEach(l => _dbgLog(l));
                if (data.Success) {
                    _dbgLog(`✅ Compiled! DLL: ${data.DllPath}`);
                    _dbgSetStatus('READY', '#22c55e');
                    document.getElementById('dbgStartBtn').disabled = false;
                } else {
                    _dbgLog(`❌ Compile failed: ${data.Error}`);
                    _dbgSetStatus('ERROR', '#ef4444');
                }
            } catch (e) {
                _dbgLog('❌ ' + e.message);
                _dbgSetStatus('ERROR', '#ef4444');
            }
        }

        // --- Start Debug Session ---
        function dbgStart() {
            if (!_debugTarget) { _dbgLog('❌ No debug target.'); return; }
            if (_debugWs) { _debugWs.close(); }

            _dbgLog('🔌 Connecting to debug server...');
            _dbgSetStatus('CONNECTING', '#f59e0b');

            // Collect the config from the custom tools form fields  
            let configObj = {};
            try {
                const fields = document.querySelectorAll('[data-config-key]');
                fields.forEach(f => {
                    const key = f.getAttribute('data-config-key');
                    configObj[key] = f.type === 'checkbox' ? f.checked : f.value;
                });
            } catch { }

            const wsUrl = `ws://${location.host}/api/debug-ws`;
            _debugWs = new WebSocket(wsUrl);

            _debugWs.onopen = () => {
                _dbgLog('✅ WebSocket connected. Starting session...');
                _debugWs.send(JSON.stringify({
                    type: 'start_debug',
                    toolName: _debugTarget.name,
                    config: configObj
                }));
                _enableDebugButtons(true);
                _dbgSetStatus('RUNNING', '#22c55e');
            };

            _debugWs.onmessage = (e) => _handleDapMessage(e.data);

            _debugWs.onerror = (e) => {
                _dbgLog('❌ WebSocket error.');
                _dbgSetStatus('ERROR', '#ef4444');
            };

            _debugWs.onclose = () => {
                _dbgLog('🔌 Session ended.');
                _enableDebugButtons(false);
                _dbgSetStatus('IDLE', '#94a3b8');
            };
        }

        function dbgStop() {
            if (_debugWs) {
                _debugWs.send(JSON.stringify({ type: 'stop_debug' }));
                _debugWs.close();
            }
        }

        function dbgSend(command) {
            if (!_debugWs || _debugWs.readyState !== WebSocket.OPEN) return;
            _dbgSendDap(command, { threadId: _dbgThreadId });
        }

        // --- DAP Protocol ---
        function _dbgSendDap(command, args = {}) {
            if (!_debugWs || _debugWs.readyState !== WebSocket.OPEN) return;
            const msg = { seq: _dbgSeq++, type: 'request', command, arguments: args };
            _debugWs.send(JSON.stringify(msg));
        }

        function _handleDapMessage(raw) {
            let msg;
            try { msg = JSON.parse(raw); } catch { _dbgLog('[raw] ' + raw); return; }

            // Internal error/status messages from our proxy layer
            if (msg.type === 'error') { _dbgLog('❌ ' + msg.message); return; }
            if (msg.type === 'debugger_started') { _dbgLog('⚡ ' + msg.message); return; }

            // DAP events
            if (msg.type === 'event') {
                switch (msg.event) {
                    case 'initialized':
                        _dbgLog('🐛 Debugger initialized. Sending breakpoints...');
                        _dbgSendDap('setBreakpoints', {
                            source: { path: '__workflow__' },
                            breakpoints: _breakpoints.map(l => ({ line: l }))
                        });
                        _dbgSendDap('configurationDone', {});
                        break;
                    case 'stopped':
                        _dbgThreadId = msg.body?.threadId;
                        _dbgLog(`⏸ Stopped: ${msg.body?.reason} (thread ${_dbgThreadId})`);
                        _dbgSetStatus('PAUSED', '#f59e0b');
                        // Get stack trace
                        _dbgSendDap('stackTrace', { threadId: _dbgThreadId });
                        break;
                    case 'continued':
                        _dbgSetStatus('RUNNING', '#22c55e');
                        break;
                    case 'terminated':
                    case 'exited':
                        _dbgLog('🏁 Process terminated.');
                        _dbgSetStatus('FINISHED', '#64748b');
                        _enableDebugButtons(false);
                        break;
                    case 'output':
                        _dbgLog('[out] ' + (msg.body?.output || '').trim());
                        break;
                }
            }

            // DAP responses
            if (msg.type === 'response') {
                if (msg.command === 'stackTrace' && msg.body?.stackFrames) {
                    _renderCallStack(msg.body.stackFrames);
                    const topFrame = msg.body.stackFrames[0];
                    if (topFrame?.line) _highlightLine(topFrame.line);
                    if (topFrame?.id) {
                        _dbgSendDap('scopes', { frameId: topFrame.id });
                    }
                }
                if (msg.command === 'scopes' && msg.body?.scopes) {
                    msg.body.scopes.forEach(scope => {
                        _dbgSendDap('variables', { variablesReference: scope.variablesReference });
                    });
                }
                if (msg.command === 'variables' && msg.body?.variables) {
                    _renderVariables(msg.body.variables);
                }
            }
        }

        function _renderCallStack(frames) {
            const el = document.getElementById('dbgCallStack');
            el.innerHTML = frames.map(f =>
                `<div style="padding:3px 0; border-bottom:1px solid rgba(255,255,255,0.04);">
                    <span style="color:#a78bfa;">${f.name}</span>
                    <span style="color:#475569; font-size:11px;"> :${f.line}</span>
                </div>`
            ).join('');
        }

        function _renderVariables(vars) {
            const el = document.getElementById('dbgVariables');
            el.innerHTML = vars.map(v =>
                `<div style="padding:2px 0;">
                    <span style="color:#38bdf8;">${v.name}</span>
                    <span style="color:#475569;"> = </span>
                    <span style="color:#a3e635;">${v.value}</span>
                    <span style="color:#475569; font-size:11px;"> (${v.type})</span>
                </div>`
            ).join('');
        }

        // --- Helpers ---
        function _dbgLog(msg) {
            const el = document.getElementById('dbgOutput');
            el.innerHTML += `<div>${msg}</div>`;
            el.scrollTop = el.scrollHeight;
        }

        function _dbgSetStatus(text, color) {
            const badge = document.getElementById('dbgStatusBadge');
            badge.textContent = text;
            badge.style.background = color + '33';
            badge.style.color = color;
        }

        function _enableDebugButtons(running) {
            ['dbgContinueBtn', 'dbgStepOverBtn', 'dbgStepInBtn', 'dbgStepOutBtn', 'dbgStopBtn']
                .forEach(id => document.getElementById(id).disabled = !running);
            document.getElementById('dbgStartBtn').disabled = running;
        }

        // Run init on load
        window.addEventListener('DOMContentLoaded', init);

    